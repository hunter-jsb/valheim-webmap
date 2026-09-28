External requirements:

1. Extract the current release of [BepInEx](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip)
   to this directory and rename to `BepInEx`
2. Create a directory named `valheim` and copy these files from your Valheim
   installation (`valheim_server_Data/Managed/`) into it:
   ```
   assembly_utils.dll
   assembly_valheim.dll
   Mono.Security.dll
   SoftReferenceableAssets.dll
   Splatform.dll
   com.rlabrecque.steamworks.net.dll
   UnityEngine.AnimationModule.dll
   UnityEngine.CoreModule.dll
   UnityEngine.dll
   UnityEngine.JSONSerializeModule.dll
   ```
3. Nothing to publicize by hand: the build does it (Krafs.Publicizer on `assembly_valheim`
   and `assembly_utils`, see `WebMap/WebMap.csproj`).
