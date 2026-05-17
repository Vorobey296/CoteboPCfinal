using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace CoteboPC;

public static class ConfigService
{
    private static string ConfigPath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoteboPC", "config.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "CoteboPC", "config.json");

    public static bool ConfigExists() => File.Exists(ConfigPath);

    public static string GetConfigPath() => ConfigPath;

    public static async Task SaveConfig(string token, long chatId)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var config = new AppConfig
        {
            Token = token,
            ChatId = chatId
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
        Logger.Log($"[Config] Saved config to {ConfigPath}");
    }

    public static async Task<AppConfig?> LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            Logger.Log($"[Config] Config file not found at {ConfigPath}");
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfig>(json);

            if (config == null)
            {
                Logger.Log($"[Config] Failed to deserialize config from {ConfigPath}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(config.Token) || config.ChatId == 0)
            {
                Logger.Log($"[Config] Invalid config loaded (Token empty or ChatId zero)");
                return null;
            }

            try
            {
                config.Token = Decrypt(config.Token);
                Logger.Log("[Config] Token was decrypted from stored config.");
            }
            catch (Exception)
            {
                Logger.Log("[Config] Token decryption failed; using raw token from config.");
            }

            Logger.Log($"[Config] Loaded config from {ConfigPath}");
            return config;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] LoadConfig exception: {ex}");
            return null;
        }
    }

    private static string Encrypt(string plainText)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        else
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            // Use SHA256 to get a valid 32-byte key
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            aes.Key = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes("CoteboPC2026SecretKey"));
            aes.IV = new byte[16];
            using var encryptor = aes.CreateEncryptor();
            byte[] data = System.Text.Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
            return Convert.ToBase64String(encrypted);
        }
    }

    private static string Decrypt(string encryptedText)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            byte[] encrypted = Convert.FromBase64String(encryptedText);
            byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        else
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            aes.Key = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes("CoteboPC2026SecretKey"));
            aes.IV = new byte[16];
            using var decryptor = aes.CreateDecryptor();
            byte[] encrypted = Convert.FromBase64String(encryptedText);
            byte[] data = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            return System.Text.Encoding.UTF8.GetString(data);
        }
    }
}
