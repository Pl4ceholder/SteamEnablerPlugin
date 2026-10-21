using System.IO.Compression;
using System.Reflection;
using Terraria;
using Terraria.Net.Sockets;
using Terraria.Social;
using TerrariaApi.Server;

namespace SteamEnablerPlugin
{
    [ApiVersion(2, 1)]
    public class SteamEnabler : TerrariaPlugin
    {
        /// <summary>
        /// The Steamworks.NET version that ships with the supported Terraria version.
        /// Terraria 1.4.5.8 references Steamworks.NET 2024.8.0.0.
        /// </summary>
        private static readonly Version SupportedSteamworksNetVersion = new(2024, 8, 0, 0);

        public override string Name { get; } = typeof(SteamEnabler).Assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? nameof(SteamEnablerPlugin);

        public override string Description { get; } = typeof(SteamEnabler).Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";

        public override Version Version { get; } = typeof(SteamEnabler).Assembly.GetName()?.Version ?? new Version(1, 0);

        public override string Author { get; } = typeof(SteamEnabler).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Arthri";

        public SteamEnabler(Main game) : base(game)
        {
        }

        public override void Initialize()
        {
            On.Terraria.Net.Sockets.SocialSocket.Terraria_Net_Sockets_ISocket_AsyncReceive += Terraria_Net_Sockets_SocialSocket__Terraria_Net_Sockets_ISocket_AsyncReceive;
            On.Terraria.Net.Sockets.SocialSocket.Terraria_Net_Sockets_ISocket_AsyncSend += Terraria_Net_Sockets_SocialSocket__Terraria_Net_Sockets_ISocket_AsyncSend;

            Console.WriteLine($"Initializing {Name}");

            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists("steam_api64.dll"))
                {
                    Download_SteamworksNET(out string? path);
                    File.Copy(
                        Path.Combine(path, "Windows-x64", "steam_api64.dll"),
                        "steam_api64.dll"
                    );
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (!Directory.Exists("steam_api.bundle"))
                {
                    Download_SteamworksNET(out string? path);
                    var bundlePath = Path.Combine(path, "OSX-Linux-x64", "steam_api.bundle");
                    foreach (string directory in Directory.GetDirectories(bundlePath, "*", SearchOption.AllDirectories))
                    {
                        Directory.CreateDirectory(directory.Replace(path, Environment.CurrentDirectory));
                    }

                    foreach (string file in Directory.GetFiles(bundlePath, "*", SearchOption.AllDirectories))
                    {
                        File.Copy(file, file.Replace(path, Environment.CurrentDirectory), true);
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (!File.Exists("libsteam_api.so"))
                {
                    Download_SteamworksNET(out string? path);
                    File.Copy(
                        Path.Combine(path, "OSX-Linux-x64", "libsteam_api.so"),
                        "libsteam_api.so"
                    );
                }
            }
            else
            {
                Console.WriteLine("UNSUPPORTED OPERATING SYSTEM");
            }

            {
                using var fs = new FileStream("steam_appid.txt", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (fs.Length <= 0)
                {
                    using var writer = new StreamWriter(fs);
                    writer.Write("105600");
                }
                else
                {
                    using var reader = new StreamReader(fs);
                    var appId = reader.ReadToEnd();
                    if (appId != "105600")
                    {
                        Console.WriteLine("\"steam_appid.txt\" contains unexpected content");
                    }
                }
            }

            Initialize_Steam();

            AppDomain.CurrentDomain.ProcessExit += (sender, args) => SocialAPI.Shutdown();
        }

        private void Terraria_Net_Sockets_SocialSocket__Terraria_Net_Sockets_ISocket_AsyncReceive(
            On.Terraria.Net.Sockets.SocialSocket.orig_Terraria_Net_Sockets_ISocket_AsyncReceive orig,
            SocialSocket self,
            byte[] data,
            int offset,
            int size,
            SocketReceiveCallback callback,
            object state
        )
        {
            // Terraria 1.4.5.8 still uses Delegate.BeginInvoke here, which is
            // not supported on .NET. Mirror the vanilla behavior with the
            // thread pool instead.
            Task.Run(() => self.ReadCallback(data, offset, size, callback, state));
        }

        private void Terraria_Net_Sockets_SocialSocket__Terraria_Net_Sockets_ISocket_AsyncSend(
            On.Terraria.Net.Sockets.SocialSocket.orig_Terraria_Net_Sockets_ISocket_AsyncSend orig,
            SocialSocket self,
            byte[] data,
            int offset,
            int size,
            SocketSendCallback callback,
            object state
        )
        {
            // Terraria 1.4.5.8 still uses Delegate.BeginInvoke for the send
            // callback, which is not supported on .NET.
            SocialAPI.Network.Send(self._remoteAddress, data, size);
            Task.Run(() => callback(state));
        }

        /// <summary>
        /// Downloads Steamworks.NET to the temporary folder and returns its path.
        /// </summary>
        /// <param name="path">The path to the unzipped Steamworks.NET files.</param>
        private static void Download_SteamworksNET(out string path)
        {
            var dependency_SteamworksNET = typeof(Projectile).Assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(static d => d.Name == "Steamworks.NET");
            if (dependency_SteamworksNET == null)
            {
                Console.WriteLine("Unsupported Terraria version (Terraria doesn't depend on Steamworks.NET.dll).");
                Environment.Exit(1);
                path = null;
                return;
            }

            var version_SteamworksNET = dependency_SteamworksNET.Version;
            if (version_SteamworksNET == null)
            {
                Console.WriteLine($"Steamworks.NET version is undefined. Assuming {SupportedSteamworksNetVersion}.");
                version_SteamworksNET = SupportedSteamworksNetVersion;
            }
            if (version_SteamworksNET != SupportedSteamworksNetVersion)
            {
                Console.WriteLine($"Unsupported Steamworks.NET version \"{version_SteamworksNET}\". Experimentally downloading version nonetheless.");
            }

            var downloadVersion = version_SteamworksNET.Revision == 0
                ? version_SteamworksNET.ToString(3)
                : version_SteamworksNET.ToString()
                ;
            var downloadFolder = Path.Combine(Path.GetTempPath(), "Steamworks.NET");
            Directory.CreateDirectory(downloadFolder);
            var unzippedSteamworksNETPath = Path.Combine(downloadFolder, downloadVersion);
            var zippedSteamworksNETPath = $"{unzippedSteamworksNETPath}.zip";

            // An earlier run may have been interrupted, so only reuse a
            // directory that contains the native library for this platform.
            if (HasSteamNativeLibrary(unzippedSteamworksNETPath))
            {
                path = unzippedSteamworksNETPath;
                return;
            }
            TryDeleteDirectory(unzippedSteamworksNETPath);

            if (File.Exists(zippedSteamworksNETPath))
            {
                try
                {
                    ZipFile.ExtractToDirectory(zippedSteamworksNETPath, unzippedSteamworksNETPath);
                    if (HasSteamNativeLibrary(unzippedSteamworksNETPath))
                    {
                        path = unzippedSteamworksNETPath;
                        return;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Unable to extract preexisting zip \"{zippedSteamworksNETPath}\". Redownloading zip file.");
                    Console.WriteLine(e.ToString());
                }

                TryDeleteDirectory(unzippedSteamworksNETPath);
                TryDeleteFile(zippedSteamworksNETPath);
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var response = http
                    .GetAsync($"https://github.com/rlabrecque/Steamworks.NET/releases/download/{downloadVersion}/Steamworks.NET-Standalone_{downloadVersion}.zip")
                    .ConfigureAwait(false).GetAwaiter().GetResult()
                    .EnsureSuccessStatusCode()
                    ;
                using (var ns = response.Content.ReadAsStream())
                using (var fs = new FileStream(zippedSteamworksNETPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // Copy synchronously: extracting while the download is
                    // still in flight truncates the archive.
                    ns.CopyTo(fs);
                    fs.Flush(true);
                }

                ZipFile.ExtractToDirectory(zippedSteamworksNETPath, unzippedSteamworksNETPath);
                if (!HasSteamNativeLibrary(unzippedSteamworksNETPath))
                {
                    throw new InvalidOperationException("The Steamworks.NET archive did not contain the native library for this platform.");
                }

                path = unzippedSteamworksNETPath;
            }
            catch
            {
                Console.WriteLine("Unable to download or extract Steamworks.NET.");
                TryDeleteFile(zippedSteamworksNETPath);
                TryDeleteDirectory(unzippedSteamworksNETPath);
                throw;
            }
        }

        /// <summary>
        /// Checks whether a Steamworks.NET directory contains the native
        /// library required by the current platform.
        /// </summary>
        private static bool HasSteamNativeLibrary(string root)
        {
            if (OperatingSystem.IsWindows())
            {
                return File.Exists(Path.Combine(root, "Windows-x64", "steam_api64.dll"));
            }
            if (OperatingSystem.IsMacOS())
            {
                return Directory.Exists(Path.Combine(root, "OSX-Linux-x64", "steam_api.bundle"));
            }
            if (OperatingSystem.IsLinux())
            {
                return File.Exists(Path.Combine(root, "OSX-Linux-x64", "libsteam_api.so"));
            }
            return false;
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                }
            }
            catch
            {
            }
        }

        // Steam-related code must run in a separate method to prevent JIT
        // from compiling Steamworks.NET and trying to find a possibly
        // missing steam_api64.dll
        private static void Initialize_Steam()
        {
            if (Terraria.Program.LaunchParameters.TryGetValue("-steam", out _))
            {
                SocialAPI.Shutdown();

                // Detects if Steam is inaccessible.
                // The Terraria client checks for this, but the error is given
                // in the form of a MessageBox which is ignored by OTAPI.
                if (!Steamworks.SteamAPI.Init())
                {
                    Console.WriteLine("Could not initialize Steam.");
                    Console.WriteLine("Possible reasons include but are not limited to");
                    Console.WriteLine("  The Steam client isn't running;");
                    Console.WriteLine("  The Steam client isn't running in the same OS user context;");
                    Console.WriteLine("  The Steam client couldn't find Terraria's app ID. Please add a \"steam_appid.txt\" file with the contents \"105600\" in the server's working directory;");
                    Console.WriteLine("  The \"steam_appid.txt\" file exists but contains the wrong content;");
                    Console.WriteLine("  The Steam client doesn't have access to run Terraria;");
                    Console.WriteLine("  For more information, visit https://partner.steamgames.com/doc/api/steam_api#SteamAPI_Init.");
                    Environment.Exit(0);
                }

                SocialAPI.Initialize(SocialMode.Steam);
            }
        }
    }
}
