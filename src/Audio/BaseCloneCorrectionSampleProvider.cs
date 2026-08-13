using NAudio.Wave;

namespace Resonance.Audio;

// Auditioned conservative minimum-phase correction for Base-cloned output.
// The 512-tap truncation differs from the 2048-tap design by at most 0.018 dB
// while avoiding unnecessary work on the game's audio callback.
internal sealed class BaseCloneCorrectionSampleProvider : ISampleProvider
{
    private const int ExpectedSampleRate = 24_000;
    private static readonly float[] Coefficients = DecodeCoefficients();
    private readonly ISampleProvider source;
    private readonly float[] history = new float[Coefficients.Length];
    private float[] sourceBuffer = [];
    private int historyIndex;
    private int tailRemaining;
    private bool sourceEnded;
    private bool receivedInput;

    public WaveFormat WaveFormat => source.WaveFormat;

    internal BaseCloneCorrectionSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.SampleRate != ExpectedSampleRate || source.WaveFormat.Channels != 1)
            throw new ArgumentException("Base clone correction requires 24 kHz mono PCM", nameof(source));
        this.source = source;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Output range exceeds the buffer");
        if (count == 0) return 0;

        if (!sourceEnded)
        {
            if (sourceBuffer.Length < count) sourceBuffer = new float[count];
            var read = source.Read(sourceBuffer, 0, count);
            if (read > 0)
            {
                receivedInput = true;
                for (var index = 0; index < read; index++)
                    buffer[offset + index] = Filter(sourceBuffer[index]);
                return read;
            }

            sourceEnded = true;
            tailRemaining = receivedInput ? Coefficients.Length - 1 : 0;
        }

        var written = Math.Min(count, tailRemaining);
        for (var index = 0; index < written; index++)
            buffer[offset + index] = Filter(0f);
        tailRemaining -= written;
        return written;
    }

    private float Filter(float sample)
    {
        history[historyIndex] = sample;
        var sum = 0f;
        var sampleIndex = historyIndex;
        for (var coefficientIndex = 0; coefficientIndex < Coefficients.Length; coefficientIndex++)
        {
            sum += Coefficients[coefficientIndex] * history[sampleIndex];
            if (--sampleIndex < 0) sampleIndex = history.Length - 1;
        }
        if (++historyIndex == history.Length) historyIndex = 0;
        return sum;
    }

    private static float[] DecodeCoefficients()
    {
        const string encoded =
        "x2Z/PxGYmLs85Za7klKUu1zmkLvfqIy7PKSHu1vkgbt57Xa7s9Rou+SeWbu7bkm7s2g4u6WyJrtccxS7KtIBu+fs3bqQDri66lWS" +
        "uo0aWrrR9xC6V5WTubYsordt/nI5qX32OXgONjoa3Ww6F7OPOi25pjrQa7s6rrzNOgmk3TqQIOs6Hzf2OnTy/jpnsQI7w84EO0re" +
        "BTsG7wU7zhEFO/1YAzso2AA7sUf7On2i8zrc6+o6LE/hOoP31jotD8w6Pb/AOiMvtTpQhKk63+GdOldokjprNYc6pcd4OlIWZDq0" +
        "f1A6MiY+Os4lLTo6lB06D4EPOgX2AjqT7u85pQfdOZcrzTn+RcA5nzq2OVbmrjkTIKo5zbmnOXCBpznOQak5fcOsObDNsTkAJ7g5" +
        "I5a/OZbixzk61dA51DjaOYTa4zksiu05vRr3OT0xADqZnQQ6K8EIOo+MDDpF8g86tuYSOilgFTrAVhc6Z8QYOsikGTo39Rk6nrQZ" +
        "OmrjGDptgxc6zpcVOuokEzo8MBA6Q8AMOmfcCDrbjAQ6ELX/OdCd9Tni5+o57affORnz0znW3sc5poC7OePtrjmJO6I5AH6VOefI" +
        "iDnEXXg52oJfOVwhRzmfVi85uDwYOT7qATlC5Ng4+savOBWTiDhFrkY4vTMAOMrSdjcjgl+0QZ5ut2Ce5bfwjSa41iVXuOdwgrgB" +
        "Cpi4/omsuGkewLiS9dK4mD3luG8j97j1aAS5bTgNuRkSFrmjBR+5DyAouXdrMbnJ7jq5qq1EuVWoTrmY21i54EBjuVbObbkMd3i5" +
        "nZWBuUnshrlGNYy5cGWRufFwlrmDS5u5q+ifufs7pLlNOai5BdWruUoEr7lAvbG5O/ezueqqtbl/0ra502m3uXVut7nA37a54b61" +
        "udAOtLlO1LG5yBWvuUXbq7k+Lqi5ehmkuduon7kw6Zq5++eVuTizkLkkWYu5AuiFud1tgLms8HW50yhruYGcYLkHYla5aY1MuRsw" +
        "Q7nLWDq5QBMyuT5oKrl+XSO5tfUcuagwF7lHCxK54n8NuVmGCblhFAa5yx0DudaUALkB1fy4vh35uOzi9bgcA/O4EV3wuGXQ7bgX" +
        "Puu4GYnouMeW5bhNT+K4+p3euINx2rgtvNW453PQuE2SyriYFMS4gPu8uAdLtbg/Cq24+kKkuHMBm7j0U5G4bEqHuBrsebjE0WS4" +
        "zmpPuE/cObjASiS4UdkOuJJS87cHs8m39Auht6wdc7dXzCa3b9S6tvZ0ubVp+C42aoTWNuVRJzdj7l83YpKKN0+GozdG47o3Tb3Q" +
        "N0kr5TcsRvg3EBQFOOF1DTiqVRU4EMAcOOfAIzjyYio4q68wOBOvNjicZzw4C95BOHoVRzhYD0w4e8tQODNIVThyglk48XVdOFod" +
        "YTh+cmQ4iG5nODIKajj9PWw4ZAJuOBJQbzgQIHA48mtwOAMucDhlYW84NAJuOJ4NbDj+gWk45F5mOCqlYjj6Vl44z3dZOH0MVDgm" +
        "G044O6tHOHXFQDjEczk4TMExOFC6KTglbCE4IeUYOIY0EDhragc4Ty/9N2qb6zcrPdo3YjnJN2y1uDfg1qg3RMOZN7Ofizf7IH03" +
        "lnFlN2F0UDeLaD43cYgvN78IJDeWFxw3udsXN75zFzdR9Ro3iGwiN0vbLTfbOD03dnFQNxtmZzc99oA3heePN6RmoDcTTrI3VXPF" +
        "N1un2Tf7tu43wjUCOK9FDThdbRg4L44jODSJLjiVPzk4A5NDOCVmTTgEnVY4dx1fOIXPZjjEnW04rHVzOOBHeDhsCHw4865+OGgb" +
        "gDiTT4A45Op/OK4gfjjDSns4z3Z3OKW1cjj5Gm04Ar1mOBy0XzhSGlg48ApQOAmiRzj3+z444zQ2OEhoLTh9sCQ4SCYcOHbgEzh+" +
        "8ws4NHEEOAvR+jePyu03JODhN2Mc1zdCg803MhLFN2HAvTcOf7c3BTqyNyrYrTcmPKo3GEWnN2DPpDdwtaI3oNCgNwb6njdNC503" +
        "gd+aN81TmDc0SJU3KqCRNyJDjTf+HIg3ax6CNzJ6dje852Y3fYVVN6ZdQjc2hC03YRYXN69z/jayN8w2VuCXNs6/QzbVw6o1wsPS" +
        "tLdoCrZVx3m2kbizthk26bYccw63+C0nt5maPrc3jlS39uRot2GCe7fgKIa3I6ONt4UtlLd0yZm3Gnyetx9OordmS6W3tYKnt08F" +
        "qbeR5qm3djuqtykaqreGmam3qtCot3nWp7c1waa3Eaalt9yYpLemq6O3fe6itzJvorcrOaK3R1Wit8fJordLmqO33sektwNRprfW" +
        "Mai3N2Sqt/TfrLcHm6+3zYmyt0qftbdpzbi3RgW8t2o3v7cYVMK3gkvFtxIOyLebjMq3jrjMtzCEzre94s+3jMjQtzQr0beeAdG3" +
        "HkTQt3zszrcD9sy3gV3Kt04hx7dFQcO3v76+t46cubfu3rO3e4uttx6pprcCQJ+3e1mXt/b/jrfjPoa3N0V6t5lwZ7exG1S3fmNA" +
        "t/ZlLLfNQRi3LRYEt+gE4LbSS7i24z6RthY1VraCMgy2l8uJtfVYOLL9z381Joz5NXRuNTZmqGk2Xp2MNkX8oTbe4LQ2uUHFNh8c" +
        "0zYPdN42I1TnNmDN7Tbe9vE2ZO3zNtrS8za0zfE2NwjuNrmv6DbL8+E2VAXaNrIV0TbJVcc2H/W8NgAhsjakA6c2dMObNlyCkDY4" +
        "XYU2v9Z0Nqt8XzYow0o2BLU2NhlRIzY=";
        var bytes = Convert.FromBase64String(encoded);
        var coefficients = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, coefficients, 0, bytes.Length);
        return coefficients;
    }
}

