using Newtonsoft.Json;
using Pannella.Helpers;
using Pannella.Models;
using Pannella.Models.OpenFPGA_Cores_Inventory;

namespace Pannella.Services;

public partial class CoresService : BaseProcess
{
    private const string CORES_END_POINT = "https://openfpga-cores-inventory.github.io/analogue-pocket/api/v2/cores.json";
    private const string ZIP_FILE_NAME = "core.zip";

    private readonly string installPath;
    private readonly SettingsService settingsService;
    private readonly ArchiveService archiveService;
    private readonly AssetsService assetsService;
    private static List<Core> cores;

    public List<Core> Cores
    {
        get
        {
            if (cores == null)
            {
                string json = HttpHelper.Instance.GetHTML(CORES_END_POINT);
                Dictionary<string, List<Core>> parsed = JsonConvert.DeserializeObject<Dictionary<string, List<Core>>>(json);

                if (parsed.TryGetValue("data", out var coresList))
                {
                    cores = coresList;
                    cores.AddRange(GetLocalCores());
                }
            }

            return cores;
        }
    }

    private static List<Core> installedCores;

    public List<Core> InstalledCores
    {
        get
        {
            if (installedCores == null)
            {
                RefreshInstalledCores();
            }

            return installedCores;
        }
    }

    private static List<Core> installedCoresWithSponsors;

    public List<Core> InstalledCoresWithSponsors
    {
        get
        {
            if (installedCoresWithSponsors == null)
            {
                RefreshInstalledCores();
            }

            return installedCoresWithSponsors;
        }
    }

    public CoresService(string path, SettingsService settingsService, ArchiveService archiveService,
        AssetsService assetsService)
    {
        installPath = path;
        this.settingsService = settingsService;
        this.archiveService = archiveService;
        this.assetsService = assetsService;
    }

    public Core GetCore(string identifier)
    {
        return Cores.Find(i => i.identifier == identifier);
    }

    public bool IsInstalled(string identifier)
    {
        // Should this just check the Installed Cores collection instead?
        string localCoreFile = Path.Combine(installPath, "Cores", identifier, "core.json");

        return File.Exists(localCoreFile);
    }

    public Core GetInstalledCore(string identifier)
    {
        return InstalledCores.Find(i => i.identifier == identifier);
    }

    public void RefreshInstalledCores()
    {
        installedCores = cores.Where(c => IsInstalled(c.identifier)).ToList();
        installedCoresWithSponsors = installedCores.Where(c => c.sponsor != null).ToList();
    }

    public bool Install(Core core, bool clean = false)
    {
        if (core.repository == null)
        {
            WriteMessage("Core installed manually. Skipping.");

            return false;
        }

        if (clean && IsInstalled(core.identifier))
        {
            Delete(core.identifier, core.platform_id);
        }

        // iterate through assets to find the zip release
        if (InstallGithubAsset(core.identifier, core.platform_id, core.download_url))
        {
            ReplaceCheck(core.identifier);
            CheckForPocketExtras(core.identifier);

            return true;
        }

        return false;
    }

    public void Uninstall(string identifier, string platformId, bool nuke = false)
    {
        WriteMessage($"Uninstalling {identifier}...");

        Delete(identifier, platformId, nuke);

        settingsService.DisableCore(identifier);
        settingsService.DisablePocketExtras(identifier);
        settingsService.Save();
        RefreshInstalledCores();

        WriteMessage("Finished.");
        Divide();
    }

    public void Delete(string identifier, string platformId, bool nuke = false)
    {
        List<string> folders = new List<string> { "Cores", "Presets", "Settings" };

        foreach (string folder in folders)
        {
            string path = Path.Combine(installPath, folder, identifier);

            if (Directory.Exists(path))
            {
                WriteMessage($"Deleting {path}...");
                Directory.Delete(path, true);
            }
        }

        if (nuke)
        {
            string path = Path.Combine(installPath, "Assets", platformId, identifier);

            if (Directory.Exists(path))
            {
                WriteMessage($"Deleting {path}...");
                Directory.Delete(path, true);
            }
        }
    }
}
