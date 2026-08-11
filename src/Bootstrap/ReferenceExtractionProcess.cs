using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Resonance.Bootstrap;

public sealed record ReferenceExtractionProcessResult(int ExitCode, string StandardError);

public sealed class ReferenceExtractionProcessException : InvalidOperationException
{
    public int ExitCode { get; }
    public string Detail { get; }
    public bool ProcessMayBeRunning { get; }

    public ReferenceExtractionProcessException(int exitCode, string detail, bool processMayBeRunning = false)
        : base($"Reference extraction helper failed ({exitCode}): {detail}")
    {
        ExitCode = exitCode;
        Detail = detail;
        ProcessMayBeRunning = processMayBeRunning;
    }
}

public interface IReferenceExtractionProcessRunner
{
    Task<ReferenceExtractionProcessResult> RunAsync(
        string executablePath, string requestPath, CancellationToken token);
}

/// <summary>
/// Optional production runner contract.  Keeping the three-argument legacy
/// seam allows tests and integrations to provide a deterministic runner while
/// the real process runner confirms ownership after assigning the child to a
/// kill-on-close job.  The helper itself publishes a handshake owner record
/// before loading native code, closing the crash-before-parent-publication
/// window.
/// </summary>
public interface IReferenceExtractionOwnershipRunner
{
    Task<ReferenceExtractionProcessResult> RunAsync(
        string executablePath, string requestPath, string ownershipPath,
        string trustedHelperRoot, string requestNonce, CancellationToken token);
}

public sealed class ReferenceExtractionProcessRunner :
    IReferenceExtractionProcessRunner, IReferenceExtractionOwnershipRunner
{
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(2);
    private static readonly object RetainedJobGate = new();
    private static readonly HashSet<WindowsJob> RetainedJobs = [];

    public Task<ReferenceExtractionProcessResult> RunAsync(
        string executablePath, string requestPath, CancellationToken token) =>
        RunAsync(executablePath, requestPath, String.Empty,
            Path.GetDirectoryName(executablePath) ?? String.Empty, String.Empty, token);

    public async Task<ReferenceExtractionProcessResult> RunAsync(
        string executablePath, string requestPath, string ownershipPath,
        string trustedHelperRoot, string requestNonce, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new ReferenceExtractionProcessException(-1,
                "reference extraction helper requires Windows/Wine job containment");

        var helperRootCandidate = String.IsNullOrWhiteSpace(trustedHelperRoot)
            ? Path.GetDirectoryName(executablePath) ?? String.Empty
            : trustedHelperRoot;
        var helperRoot = ValidateTrustedPath(
            helperRootCandidate,
            Path.GetDirectoryName(helperRootCandidate) ?? helperRootCandidate,
            "helper root", requireExisting: true, directory: true);
        var helper = ValidateTrustedPath(executablePath, helperRoot, "helper executable", true, false);
        var request = ValidateTrustedPath(requestPath,
            Path.GetDirectoryName(requestPath) ?? String.Empty, "request", true, false);
        var owner = String.IsNullOrWhiteSpace(ownershipPath)
            ? null
            : ValidateTrustedPath(ownershipPath,
                Path.GetDirectoryName(ownershipPath) ?? String.Empty,
                "ownership metadata", false, false);
        if (new FileInfo(request).Length > ReferenceExtractionProtocol.MaximumRequestBytes)
            throw new InvalidDataException("Reference extraction request is too large");
        var requestDocument = JsonSerializer.Deserialize<ReferenceExtractionRequest>(
            File.ReadAllText(request), ReferenceExtractionProtocol.JsonOptions())
            ?? throw new InvalidDataException("Reference extraction request is empty");
        ReferenceExtractionProtocol.ValidateRequest(requestDocument, requireInput: true);
        if (!String.IsNullOrWhiteSpace(requestNonce)
            && !String.Equals(requestNonce, requestDocument.RequestNonce, StringComparison.Ordinal))
            throw new InvalidDataException("Reference extraction ownership nonce does not match the request");
        ReferenceExtractionProtocol.ValidateTransientPath(
            request, requestDocument.TrustedReferenceRoot, "request path");
        if (owner is not null)
        {
            ReferenceExtractionProtocol.ValidateTransientPath(
                owner, requestDocument.TrustedReferenceRoot, "ownership metadata");
            ReferenceExtractionProtocol.ValidateTransientPath(
                owner + ".part", requestDocument.TrustedReferenceRoot,
                "ownership metadata temporary");
        }
        var launchPermit = Path.Combine(
            Path.GetDirectoryName(request) ?? throw new InvalidDataException("request directory is missing"),
            "launch.ready");
        ReferenceExtractionProtocol.ValidateTransientPath(
            launchPermit, requestDocument.TrustedReferenceRoot, "launch permit");
        token.ThrowIfCancellationRequested();

        using var job = WindowsJob.Create();
        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(request);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Reference extraction helper did not start");
        var standardError = process.StandardError.ReadToEndAsync();
        var assigned = false;
        string assignmentError;
        try
        {
            assigned = job.TryAssign(process, out assignmentError);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception
                                      or NotSupportedException)
        {
            assignmentError = $"AssignProcessToJobObject could not be attempted: {error.Message}";
        }
        if (!assigned)
        {
            // Assignment failure means the job never became a containment
            // boundary.  Do not treat closing that job as proof that the
            // child tree is gone; use the verified process-tree termination
            // path and retain ownership files when it cannot prove exit.
            var termination = await TerminateUncontainedAsync(process, job).ConfigureAwait(false);
            var processErrorText = await ReadStandardErrorAsync(standardError).ConfigureAwait(false);
            var detail = String.IsNullOrWhiteSpace(processErrorText)
                ? assignmentError
                : $"{assignmentError}; helper stderr: {processErrorText}";
            throw new ReferenceExtractionProcessException(-1, detail,
                processMayBeRunning: !termination.Terminated);
        }

        if (owner is not null)
        {
            try
            {
                // The helper owns publication of its PID/start identity.  Do
                // not race it by writing the same temporary file from the
                // parent; wait for its atomic record before granting the
                // launch permit.
                await WaitForOwnershipAsync(owner, process, requestNonce, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var termination = await TerminateAsync(process, job).ConfigureAwait(false);
                var cancellationErrorText = await ReadStandardErrorAsync(standardError).ConfigureAwait(false);
                if (!termination.Terminated)
                {
                    var detail = String.IsNullOrWhiteSpace(cancellationErrorText)
                        ? termination.Detail
                        : $"{termination.Detail}; helper stderr: {cancellationErrorText}";
                    throw new ReferenceExtractionProcessException(-1, detail, processMayBeRunning: true);
                }
                throw;
            }
            catch (Exception ownershipError) when (ownershipError is IOException or UnauthorizedAccessException
                                                    or InvalidDataException or InvalidOperationException
                                                    or Win32Exception
                                                    or TimeoutException)
            {
                var termination = await TerminateAsync(process, job).ConfigureAwait(false);
                var ownershipErrorText = await ReadStandardErrorAsync(standardError).ConfigureAwait(false);
                var detail = $"ownership metadata was not published: {ownershipError.Message}";
                if (!String.IsNullOrWhiteSpace(ownershipErrorText)) detail += $"; helper stderr: {ownershipErrorText}";
                throw new ReferenceExtractionProcessException(-1, detail,
                    processMayBeRunning: !termination.Terminated);
            }
        }

        try
        {
            WriteLaunchPermit(launchPermit, requestDocument.TrustedReferenceRoot,
                requestDocument.RequestNonce);
        }
        catch (Exception permitError) when (permitError is IOException or UnauthorizedAccessException
                                             or InvalidDataException or DirectoryNotFoundException)
        {
            var termination = await TerminateAsync(process, job).ConfigureAwait(false);
            throw new ReferenceExtractionProcessException(-1,
                $"launch permit could not be persisted: {permitError.Message}",
                processMayBeRunning: !termination.Terminated);
        }

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var termination = await TerminateAsync(process, job).ConfigureAwait(false);
            var cancellationErrorText = await ReadStandardErrorAsync(standardError).ConfigureAwait(false);
            if (!termination.Terminated)
            {
                var detail = String.IsNullOrWhiteSpace(cancellationErrorText)
                    ? termination.Detail
                    : $"{termination.Detail}; helper stderr: {cancellationErrorText}";
                throw new ReferenceExtractionProcessException(-1, detail, processMayBeRunning: true);
            }
            throw;
        }

        var processError = await ReadStandardErrorAsync(standardError).ConfigureAwait(false);
        if (!await job.DisposeConfirmedAsync().ConfigureAwait(false))
        {
            RetainJobForReaper(job);
            throw new ReferenceExtractionProcessException(
                -1, "helper job handle close was not confirmed after normal exit",
                processMayBeRunning: true);
        }
        return new(process.ExitCode, processError.Trim());
    }

    private static async Task<TerminationResult> TerminateAsync(Process process, WindowsJob job)
    {
        if (!job.IsAssigned)
            return new(false, "helper job was not assigned; descendant termination is unverified");
        var errors = new List<string>();
        var jobTerminated = job.TryTerminate(out var jobError);
        if (!jobTerminated && !String.IsNullOrWhiteSpace(jobError)) errors.Add(jobError);
        if (!jobTerminated && !HasExited(process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            { errors.Add($"process-tree termination failed: {error.Message}"); }
        }
        var exited = await WaitForExitAsync(process).ConfigureAwait(false);
        // Closing a kill-on-close job is confirmed separately.  A failed
        // close retains the handle and therefore prevents the caller from
        // deleting transient ownership files.
        var jobClosed = await job.DisposeConfirmedAsync().ConfigureAwait(false);
        if (!jobClosed) RetainJobForReaper(job);
        if (jobClosed && !exited)
            exited = await WaitForExitAsync(process).ConfigureAwait(false);
        if (exited && jobClosed)
            return new(true, jobTerminated
                ? "helper job terminated and job handle closed"
                : "helper job closed after process termination");
        if (!exited) errors.Add("helper process did not exit within the termination grace period");
        if (!jobClosed) errors.Add("helper job handle close was not confirmed");
        return new(false, String.Join("; ", errors.Where(value => !String.IsNullOrWhiteSpace(value))));
    }

    private static async Task<TerminationResult> TerminateUncontainedAsync(Process process, WindowsJob job)
    {
        var errors = new List<string>();
        var wasAlive = !HasExited(process);
        var treeKillIssued = false;
        if (wasAlive)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                treeKillIssued = true;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                errors.Add($"uncontained process-tree termination failed: {error.Message}");
            }
        }
        else
        {
            // The parent exited before it was assigned to the job.  Its
            // descendants cannot be proven dead through the parent handle;
            // retain the run as abandoned rather than trusting job close.
            errors.Add("uncontained helper exited before tree containment could be verified");
        }
        var exited = await WaitForExitAsync(process).ConfigureAwait(false);
        var jobClosed = await job.DisposeConfirmedAsync().ConfigureAwait(false);
        if (!jobClosed) RetainJobForReaper(job);
        if (treeKillIssued && exited && jobClosed)
        {
            // Process.Kill(entireProcessTree: true) only gives us a bounded
            // parent-handle observation.  Without a job assignment there is
            // no authoritative descendant lifetime to inspect, so retain the
            // run as abandoned even when the helper parent has exited.
            return new(false, "uncontained helper parent exited; descendant termination was not verifiable");
        }
        if (!exited) errors.Add("uncontained helper process did not exit within the termination grace period");
        if (!jobClosed) errors.Add("helper job handle close was not confirmed");
        return new(false, String.Join("; ", errors.Where(value => !String.IsNullOrWhiteSpace(value))));
    }

    private static void RetainJobForReaper(WindowsJob job)
    {
        lock (RetainedJobGate)
        {
            if (!RetainedJobs.Add(job)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    job.TryTerminate(out _);
                    if (await job.DisposeConfirmedAsync().ConfigureAwait(false))
                    {
                        lock (RetainedJobGate) RetainedJobs.Remove(job);
                        return;
                    }
                    await Task.Delay(TerminationWait).ConfigureAwait(false);
                }
            }
            catch { }
        });
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (ObjectDisposedException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task<bool> WaitForExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TerminationWait).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (ObjectDisposedException) { return true; }
        catch (InvalidOperationException) { return HasExited(process); }
        catch (Win32Exception) { return false; }
    }

    private static async Task<string> ReadStandardErrorAsync(Task<string> standardError)
    {
        try { return (await standardError.WaitAsync(TerminationWait).ConfigureAwait(false)).Trim(); }
        catch (TimeoutException) { return "helper stderr did not close within the termination grace period"; }
        catch (ObjectDisposedException disposedError) { return disposedError.Message; }
        catch (InvalidOperationException stateError) { return stateError.Message; }
        catch (IOException ioError) { return ioError.Message; }
        catch (Exception readError) { return readError.Message; }
    }

    private static async Task WaitForOwnershipAsync(string path, Process process, string requestNonce,
        CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TerminationWait;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (HasExited(process))
                throw new InvalidOperationException("reference extraction helper exited before publishing ownership");
            if (File.Exists(path))
            {
                try
                {
                    var ownership = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                        File.ReadAllText(path));
                    if (ownership is null || ownership.ProcessId != process.Id
                        || ownership.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks
                        || !String.Equals(ownership.RequestNonce, requestNonce, StringComparison.Ordinal))
                        throw new InvalidDataException("reference extraction ownership identity does not match the helper");
                    return;
                }
                catch (JsonException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
        }
        throw new TimeoutException("reference extraction helper did not publish ownership before the launch deadline");
    }

    private static void WriteLaunchPermit(string path, string trustedRoot, string requestNonce)
    {
        _ = ReferenceExtractionProtocol.ValidateTransientPath(path, trustedRoot, "launch permit");
        var temporary = path + ".part";
        _ = ReferenceExtractionProtocol.ValidateTransientPath(
            temporary, trustedRoot, "launch permit temporary");
        File.WriteAllText(temporary, requestNonce);
        File.Move(temporary, path, true);
    }

    private static string ValidateTrustedPath(
        string value, string root, string label, bool requireExisting, bool directory)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || String.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException($"Reference extraction {label} path is invalid");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonical = Path.GetFullPath(value);
        var comparison = StringComparison.OrdinalIgnoreCase;
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!String.Equals(canonical, canonicalRoot, comparison)
            && !canonical.StartsWith(prefix, comparison))
            throw new InvalidDataException($"Reference extraction {label} escapes its trusted root");
        if (requireExisting && (directory ? !Directory.Exists(canonical) : !File.Exists(canonical)))
            throw new FileNotFoundException($"Reference extraction {label} is missing", canonical);
        var parent = directory ? canonical : Path.GetDirectoryName(canonical);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Reference extraction {label} parent is missing");
        RejectReparseComponents(canonical, label, includeLeaf: true);
        if (!directory && Directory.Exists(canonical))
            throw new InvalidDataException($"Reference extraction {label} points to a directory");
        return canonical;
    }

    private static void RejectReparseComponents(string path, string label, bool includeLeaf)
    {
        var current = Path.GetPathRoot(path);
        if (String.IsNullOrEmpty(current)) return;
        var segments = path[current.Length..].Split(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var leaf = index == segments.Length - 1;
            if (leaf && !includeLeaf) continue;
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                try
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                        throw new InvalidDataException($"Reference extraction {label} uses a reparse point");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                continue;
            }
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Reference extraction {label} uses a reparse point");
        }
    }

    private sealed record TerminationResult(bool Terminated, string Detail);

    private sealed class WindowsJob : IDisposable
    {
        private const int JobObjectInfoClassExtendedLimit = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private readonly object gate = new();
        private nint handle;
        private int assigned;

        private WindowsJob(nint handle) { this.handle = handle; }

        public static WindowsJob Create()
        {
            var handle = CreateJobObjectW(0, null);
            if (handle == 0) ThrowLastError("CreateJobObject");
            var limits = new JobObjectExtendedLimitInfo
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<JobObjectExtendedLimitInfo>());
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectInfoClassExtendedLimit, buffer,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInfo>()))
                {
                    ThrowLastError("SetInformationJobObject");
                }
                return new WindowsJob(handle);
            }
            catch
            {
                CloseHandle(handle);
                throw;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public bool TryAssign(Process process, out string error)
        {
            lock (gate)
            {
                var current = handle;
                if (current != 0 && AssignProcessToJobObject(current, process.Handle))
                {
                    assigned = 1;
                    error = String.Empty;
                    return true;
                }
                error = current == 0
                    ? "AssignProcessToJobObject attempted after the job handle closed"
                    : $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
        }

        public bool TryTerminate(out string error)
        {
            lock (gate)
            {
                var current = handle;
                if (current != 0 && TerminateJobObject(current, 1))
                {
                    error = String.Empty;
                    return true;
                }
                error = current == 0
                    ? "helper job handle is already closed"
                    : $"TerminateJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
        }

        public bool IsAssigned
        {
            get { lock (gate) return assigned != 0; }
        }

        public bool TryClose(out string error)
        {
            lock (gate)
            {
                var current = handle;
                if (current == 0)
                {
                    error = String.Empty;
                    return true;
                }
                if (!CloseHandle(current))
                {
                    error = $"CloseHandle failed: {Marshal.GetLastWin32Error()}";
                    return false;
                }
                handle = 0;
                error = String.Empty;
                return true;
            }
        }

        public async Task<bool> DisposeConfirmedAsync(CancellationToken token = default)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (TryClose(out _)) return true;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        public void Dispose()
        {
            // Do not hide an unawaited retry.  Failed close retains the
            // handle for an explicit owner/reaper.
            _ = TryClose(out _);
        }

        private static void ThrowLastError(string operation) =>
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            nint job, int informationClass, nint information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(nint job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}
