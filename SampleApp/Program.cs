using System.Diagnostics;

namespace SampleApp;

public class Program
{
    static LazyConfig? _config = null;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Make sure salt is consistent across machines
        string temp = Convert.ToBase64String("f94aaa0dacf2454fb0b8ab2aa8ec1465".ToByteArray());
        byte[] salt = Convert.FromBase64String(temp);

        var portable = new AesPortableEncryptor("super-secret-passphrase", salt);
        
        Console.WriteLine("✔️ Creating LazyConfig…");
        _config = new LazyConfig(encryptor: portable);
        var cv = _config.CompatibleVersion;
        
        // Is this our first run?
        if (string.IsNullOrEmpty(cv))
        {
            _config.CompatibleVersion = "1.0.0";
            _config.FirstRun = true;
            _config.Logging = false;
            _config.LastUse = DateTime.MinValue;
            _config.User = $"{Environment.UserDomainName}";
            _config.PositionX = 100;
            _config.PositionY = 100;
            _config.APIKey = "ThisRepresentsASampleAPIKey";
        }
        else
        {
            _config.FirstRun = false;

           Console.WriteLine($"[DECRYPTED] APIKey='{_config.APIKey}'");

            // Warn the user for unset properties
            List<string> emptyProps = Extensions.GetEmptyStringProperties(_config, "You should configure a value here.");
            if (emptyProps.Count > 0)
            {
                Console.WriteLine("⚠️ Warning: The following properties are empty:");
                foreach (var prop in emptyProps)
                    Console.WriteLine($"  - {prop}");
            }
            else
            {
                Console.WriteLine("✔️ All properties are set.");
            }
        }

        Console.WriteLine($"🔔 Press any key to exit the demo.");
        _ = Console.ReadKey(true).Key;
        Console.WriteLine("🔔 Exiting…");
        Process proc = Process.GetCurrentProcess();
        _config.Metrics = $"Process used {proc.PrivateMemorySize64 / 1024 / 1024}MB of memory and {proc.TotalProcessorTime.ToReadableString()} TotalProcessorTime on {Environment.ProcessorCount} possible cores.";
        _config.LastUse = DateTime.Now;
        Thread.Sleep(1500);
    }
}
