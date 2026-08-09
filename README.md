# Resonance

Resonance is a Dalamud plugin that locally dubs otherwise-unvoiced Final Fantasy XIV cutscene dialogue. It supports the four FFXIV client voice languages: English, Japanese, German, and French.

## Install from the custom repository

1. Open XIVLauncher/Dalamud, then open **Dalamud Settings** (`/xlsettings`).
2. Select **Experimental**.
3. Add this custom plugin repository URL:

   ```text
   https://slobodaapl.github.io/resonance/repo.json
   ```

4. Save, open **Dalamud Plugin Installer** (`/xlplugins`), search for **Resonance**, and install it.
5. Open Resonance settings. The native inference runtime and selected models download in the background; progress is shown there. They are not bundled in the plugin archive.

Resonance does not require any other Dalamud plugins, and uses entirely local AI inference for lore-friendly dubbing.

## License

Resonance's original code is available under the [MIT License](LICENSE). Third-party components retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
