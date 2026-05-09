# Lessons

- **Cuphead has a native macOS build.** Don't assume the client needs Whisky/Wine; default the Mac installer to the native Steam path `~/Library/Application Support/Steam/steamapps/common/Cuphead/` and use BepInEx_macos_universal, not BepInEx_win_x64.
- **Don't refer to the second player as "the friend".** Use "the client", "the partner", "the second player", or just "the other PC". Neutral language only.
- **Overlay vs console.** The IMGUI overlay shows mode/Rewired ids/sequence/buttons/axes; the BepInEx console shows log lines like "CoopClient: handshake ok". Don't conflate them in test instructions.
