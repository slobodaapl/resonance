# Resonance

**Local AI dubbing for Final Fantasy XIV cutscenes.**

Resonance is a Dalamud plugin that voices otherwise-unvoiced FFXIV cutscene dialogue using fully local AI inference.

It supports English, Japanese, German, and French.

Resonance focuses exclusively on cutscenes to preserve the game's original presentation everywhere else. Combat dialogue, chat, ambient NPC chatter, and speech bubbles remain untouched by design.

## Notice

Resonance is under active development! I'm still planning to add support for NPU inference on eligible Intel and Ryzen CPUs, and continue resolving bugs and quality issues.

As such please don't hesitate to submit issues, including for even things like "I don't like how this sounds, it isn't correct." -- I'm trying to get all voiced actors sounding as correct as possible, and all generic racial voices also sounding appropriate. Some races like Nu Mou are very hard to get directionally right, and there are other such cases where particular races / locales / tribes need individual attention, that I might've missed, so please do let me know so I can pay it proper attention if you discover such a case. I might not be able to get everything perfectly immersive but I'm striving for "as close as possible".

## Features

* **Canonical character voices**: Main story characters sound like their usual selves, no unimmersive random voices.
* **Persistent generated voices**: never-voiced characters receive consistent voices instead of changing between encounters.
* **Lore-aware casting**: regional, cultural, species, and character context help generated voices fit the world. You'll hear the exact accents you've come to expect in various regions.
* **Fully local**: models run on your machine with CUDA, Vulkan, or CPU fallback and are small and respectful of your RAM/VRAM. At least 2GB spare RAM/VRAM is necessary, with 3GB recommended.

## Resonance vs. Echokraut

Resonance and Echokraut approach FFXIV dubbing differently. Echokraut supports a much broader range of dialogue; Resonance deliberately concentrates on cinematic cutscenes.

| Feature                                                  | Resonance   | Echokraut                       |
| -------------------------------------------------------- | ----------- | ------------------------------- |
| Unvoiced story dialogue                                  | ✅           | ✅                               |
| Auto-advance                                             | ✅           | ✅                               |
| Local inference                                          | ✅           | ✅                               |
| EN / JA / DE / FR                                        | ✅           | ✅                               |
| Existing voiced-line detection                           | ✅           | ✅                               |
| Clone established character voices from in-game dialogue | ✅           | ❌                               |
| Persistent AI-designed NPC voices                        | ✅           | Different voice-matching system |
| Regional / cultural casting                              | ✅           | ❌                               |
| CUDA / Vulkan / CPU                                      | ✅           | Backend-dependent               |
| Cutscene-only                                            | ✅ by design | ❌                               |
| Battle dialogue                                          | ❌ by design | ✅                               |
| Speech bubbles / ambient NPCs                            | ❌ by design | ✅                               |
| Chat TTS                                                 | ❌ by design | ✅                               |

## Installation

1. Open **Dalamud Settings** (`/xlsettings`).

2. Select **Experimental**.

3. Add:

   ```text
   https://slobodaapl.github.io/resonance/repo.json
   ```

4. Open **Dalamud Plugin Installer** (`/xlplugins`), search for **Resonance**, and install it.

5. Open Resonance settings. Required models and inference components download automatically in the background.

Resonance does not require any other Dalamud plugins.

## Proton / Wine

For CUDA acceleration under Proton, launch XIVLauncher with:

```bash
PROTON_ENABLE_NVAPI=1
PROTON_NVIDIA_NVCUDA=1
```

Depending on your Linux system, version/type of Proton or Wine used, and environment, you might also need to add `nvcuda=n,b` to WINEDLLOVERRIDES in order for the CUDA backend to function correctly. I highly recommend you use XIVLauncher-RB if you are able.

If CUDA is unavailable, Resonance can use Vulkan or CPU instead.

## License

Resonance's original code is available under the [MIT License](LICENSE).

Third-party components retain their own licenses; see THIRD_PARTY_NOTICES.md.
