using Newtonsoft.Json;
using Pannella.Helpers;
using Pannella.Models;
using Pannella.Models.Extras;
using Pannella.Models.OpenFPGA_Cores_Inventory;
using File = System.IO.File;
using AnalogueCore = Pannella.Models.Analogue.Core.Core;

namespace Pannella.Services;

public class CoreUpdaterService : BaseProcess
{
    private readonly string installPath;
    private readonly List<Core> cores;
    private readonly FirmwareService firmwareService;
    private SettingsService settingsService;
    private CoresService coresService;

    public CoreUpdaterService(
        string path,
        List<Core> cores,
        FirmwareService firmwareService,
        SettingsService settingsService,
        CoresService coresService)
    {
        installPath = path;
        this.cores = cores;
        this.firmwareService = firmwareService;
        this.settingsService = settingsService;
        this.coresService = coresService;

        Directory.CreateDirectory(Path.Combine(path, "Cores"));
    }

    public void BuildInstanceJson(bool overwrite = false, string coreName = null)
    {
        foreach (Core core in cores)
        {
            if (coresService.CheckInstancePackager(core.identifier) && (coreName == null || coreName == core.identifier))
            {
                WriteMessage(core.identifier);
                coresService.BuildInstanceJson(core.identifier, overwrite);
                Divide();
            }
        }
    }

    /// <summary>
    /// Run the full openFPGA core download and update process
    /// </summary>
    public void RunUpdates(string[] ids = null, bool clean = false)
    {
        List<Dictionary<string, string>> installed = new List<Dictionary<string, string>>();
        List<string> installedAssets = new List<string>();
        List<string> skippedAssets = new List<string>();
        List<string> missingBetaKeys = new List<string>();
        string firmwareDownloaded = null;

        if (settingsService.GetConfig().backup_saves)
        {
            AssetsService.BackupSaves(installPath, settingsService.GetConfig().backup_saves_location);
            AssetsService.BackupMemories(installPath, settingsService.GetConfig().backup_saves_location);
        }

        if (settingsService.GetConfig().download_firmware && ids == null)
        {
            if (firmwareService != null)
            {
                firmwareDownloaded = firmwareService.UpdateFirmware(installPath);
            }
            else
            {
                WriteMessage("Firmware Service is missing.");
            }

            Divide();
        }

        bool jtBetaKeyExists = coresService.ExtractBetaKey();

        foreach (var core in cores.Where(core => ids == null || ids.Any(id => id == core.identifier)))
        {
            var coreSettings = settingsService.GetCoreSettings(core.identifier);

            try
            {
                if (coreSettings.skip)
                {
                    DeleteCore(core);
                    continue;
                }

                if (core.requires_license && !jtBetaKeyExists)
                {
                    missingBetaKeys.Add(core.identifier);
                    continue; // skip if you don't have the key
                }

                if (core.identifier == null)
                {
                    WriteMessage("Core Name is required. Skipping.");
                    continue;
                }

                WriteMessage("Checking Core: " + core.identifier);

                PocketExtra pocketExtra = coresService.GetPocketExtra(core.identifier);
                bool isPocketExtraCombinationPlatform = coreSettings.pocket_extras &&
                                                        pocketExtra is { type: PocketExtraType.combination_platform };
                string mostRecentRelease;

                if (core.version == null && coreSettings.pocket_extras)
                {
                    mostRecentRelease = coresService.GetMostRecentRelease(pocketExtra);
                }
                else
                {
                    mostRecentRelease = core.version;
                    pocketExtra = null;
                }

                Dictionary<string, object> results;

                if (mostRecentRelease == null && pocketExtra == null && !coreSettings.pocket_extras)
                {
                    WriteMessage("No releases found. Skipping.");

                    var isBetaCore = coresService.IsBetaCore(core.identifier);

                    if (isBetaCore.Item1)
                    {
                        core.beta_slot_id = isBetaCore.Item2;
                        core.beta_slot_platform_id_index = isBetaCore.Item3;
                        coresService.CopyBetaKey(core);
                    }

                    results = coresService.DownloadAssets(core);
                    installedAssets.AddRange(results["installed"] as List<string>);
                    skippedAssets.AddRange(results["skipped"] as List<string>);

                    if ((bool)results["missingBetaKey"])
                    {
                        missingBetaKeys.Add(core.identifier);
                    }

                    JotegoRename(core);
                    Divide();
                    continue;
                }

                WriteMessage(mostRecentRelease + " is the most recent release, checking local core...");

                if (coresService.IsInstalled(core.identifier))
                {
                    AnalogueCore localCore = coresService.ReadCoreJson(core.identifier);
                    string localVersion = isPocketExtraCombinationPlatform
                        ? coreSettings.pocket_extras_version
                        : localCore.metadata.version;

                    if (localVersion != null)
                    {
                        WriteMessage("Local core found: " + localVersion);
                    }

                    if (mostRecentRelease != localVersion || clean)
                    {
                        WriteMessage("Updating core...");
                    }
                    else
                    {
                        var isBetaCore = coresService.IsBetaCore(core.identifier);

                        if (isBetaCore.Item1)
                        {
                            core.beta_slot_id = isBetaCore.Item2;
                            core.beta_slot_platform_id_index = isBetaCore.Item3;
                            coresService.CopyBetaKey(core);
                        }

                        if (coreSettings.pocket_extras &&
                            pocketExtra != null &&
                            pocketExtra.type != PocketExtraType.combination_platform)
                        {
                            WriteMessage("Pocket Extras found: " + coreSettings.pocket_extras_version);
                            var version = coresService.GetMostRecentRelease(pocketExtra);
                            WriteMessage(version + " is the most recent release...");

                            if (coreSettings.pocket_extras_version != version)
                            {
                                WriteMessage("Updating Pocket Extras...");
                                coresService.GetPocketExtra(pocketExtra, installPath, false, false);
                            }
                            else
                            {
                                WriteMessage("Up to date. Skipping Pocket Extras.");
                            }
                        }

                        results = coresService.DownloadAssets(core);

                        if (!coreSettings.pocket_extras)
                        {
                            JotegoRename(core);
                        }

                        installedAssets.AddRange(results["installed"] as List<string>);
                        skippedAssets.AddRange(results["skipped"] as List<string>);

                        if ((bool)results["missingBetaKey"])
                        {
                            missingBetaKeys.Add(core.identifier);
                        }

                        WriteMessage("Up to date. Skipping core.");
                        Divide();
                        continue;
                    }
                }
                else
                {
                    WriteMessage("Downloading core...");
                }

                if (isPocketExtraCombinationPlatform)
                {
                    if (clean && coresService.IsInstalled(core.identifier))
                    {
                        coresService.Delete(core.identifier, core.platform_id);
                    }

                    coresService.GetPocketExtra(pocketExtra, installPath, false, false);

                    Dictionary<string, string> summary = new Dictionary<string, string>
                    {
                        { "version", mostRecentRelease },
                        { "core", core.identifier },
                        { "platform", core.platform.name }
                    };

                    installed.Add(summary);
                }
                else if (coresService.Install(core, clean))
                {
                    Dictionary<string, string> summary = new Dictionary<string, string>
                    {
                        { "version", mostRecentRelease },
                        { "core", core.identifier },
                        { "platform", core.platform.name }
                    };

                    installed.Add(summary);
                }
                else if (coreSettings.pocket_extras &&
                         pocketExtra != null &&
                         pocketExtra.type != PocketExtraType.combination_platform)
                {
                    WriteMessage("Pocket Extras found: " + coreSettings.pocket_extras_version);
                    var version = coresService.GetMostRecentRelease(pocketExtra);
                    WriteMessage(version + " is the most recent release...");

                    if (coreSettings.pocket_extras_version != version)
                    {
                        WriteMessage("Updating Pocket Extras...");
                        coresService.GetPocketExtra(pocketExtra, installPath, false, false);
                    }
                    else
                    {
                        WriteMessage("Up to date. Skipping Pocket Extras.");
                    }
                }

                JotegoRename(core);

                var isJtBetaCore = coresService.IsBetaCore(core.identifier);

                if (isJtBetaCore.Item1)
                {
                    core.beta_slot_id = isJtBetaCore.Item2;
                    core.beta_slot_platform_id_index = isJtBetaCore.Item3;
                    coresService.CopyBetaKey(core);
                }

                results = coresService.DownloadAssets(core);
                installedAssets.AddRange(results["installed"] as List<string>);
                skippedAssets.AddRange(results["skipped"] as List<string>);

                if ((bool)results["missingBetaKey"])
                {
                    missingBetaKeys.Add(core.identifier);
                }

                WriteMessage("Installation complete.");
                Divide();
            }
            catch (Exception e)
            {
                WriteMessage("Uh oh something went wrong.");
#if DEBUG
                WriteMessage(e.ToString());
#else
                WriteMessage(e.Message);
#endif
            }
        }

        coresService.DeleteBetaKey();
        coresService.RefreshLocalCores();
        coresService.RefreshInstalledCores();

        UpdateProcessCompleteEventArgs args = new UpdateProcessCompleteEventArgs
        {
            Message = "Update Process Complete.",
            InstalledCores = installed,
            InstalledAssets = installedAssets,
            SkippedAssets = skippedAssets,
            MissingBetaKeys = missingBetaKeys,
            FirmwareUpdated = firmwareDownloaded,
            SkipOutro = false,
        };

        OnUpdateProcessComplete(args);
    }

    private void JotegoRename(Core core)
    {
        if (settingsService.GetConfig().fix_jt_names &&
            settingsService.GetCoreSettings(core.identifier).platform_rename &&
            core.identifier.Contains("jotego"))
        {
            core.platform_id = core.identifier.Split('.')[1];

            string path = Path.Combine(installPath, "Platforms", core.platform_id + ".json");
            string json = File.ReadAllText(path);
            Dictionary<string, Platform> data = JsonConvert.DeserializeObject<Dictionary<string, Platform>>(json);
            Platform platform = data["platform"];

            if (coresService.RenamedPlatformFiles.TryGetValue(core.platform_id, out string value) &&
                platform.name == core.platform_id)
            {
                WriteMessage("Updating JT Platform Name...");
                HttpHelper.Instance.DownloadFile(value, path);
                WriteMessage("Complete");
            }
        }
    }

    public void DeleteCore(Core core, bool force = false, bool nuke = false)
    {
        // If the core was a pocket extra or local the core inventory won't have it's platform id.
        // Load it from the core.json file if it's missing.
        if (string.IsNullOrEmpty(core.platform_id))
        {
            var analogueCore = coresService.ReadCoreJson(core.identifier);

            core.platform_id = analogueCore.metadata.platform_ids[0];
        }

        if (settingsService.GetConfig().delete_skipped_cores || force)
        {
            coresService.Uninstall(core.identifier, core.platform_id, nuke);
        }
    }

    public void ReloadSettings()
    {
        settingsService = ServiceHelper.SettingsService;
        coresService = ServiceHelper.CoresService;
    }
}
