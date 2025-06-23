using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///   Provides a thread-safe, lazy-loaded configuration system for managing application settings.
/// </summary>
/// <remarks>
///   The <see cref="LazyConfig"/> class allows you to store and retrieve application settings as
///   key-value pairs, with support for lazy initialization and automatic persistence to a JSON file. 
///   Settings are loaded on first access and saved automatically when modified. This class is designed 
///   to be thread-safe, ensuring consistent access to settings in multi-threaded environments. 
///   The configuration file is stored in the application's base directory, the default file 
///   name is <c>settings.config</c>. We're using <see cref="string"/> for the property storage 
///   unit, it is the most versatile type for storing configurations, allowing for easier 
///   conversion by the user and during serialization/deserialization steps. The amount of disk
///   space saved by using <see cref="bool"/>, or <see cref="int"/> instead of <see cref="string"/> 
///   is negligible - e.g. <c>"FirstRun": "False"</c>, vs <c>"FirstRun": False</c>, is only 2 bytes.
/// </remarks>
public class LazyConfig
{
    #region [MEMBERS]
    static string _parity = string.Empty; // for parity during save
    readonly string _fileName;
    readonly IEncryptor _encryptor; // null ⇨ no encryption
    readonly Lazy<Dictionary<string, string>> _map;
    readonly HashSet<string> _encryptedKeys;
    readonly StringComparer _strComp = StringComparer.OrdinalIgnoreCase;
    readonly ReaderWriterLockSlim _ioLock = new ReaderWriterLockSlim();
    readonly JsonSerializerOptions _jsopts = new JsonSerializerOptions 
    {
        IncludeFields = true,
        WriteIndented = true,
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

        // You can remove this if you want to see chars like plus '+' represented as \u002B in encrypted outputs.
        // This can save some space, and make reading/debugging easier, but it does open the door for unsafe html.
        // Unlike the default encoder, this encoder instance does not escape HTML-sensitive characters such as <, >, &.
        // As a result, it must be used cautiously; for example, it can be used if the output data is within a response
        // whose content-type is known with a charset set to UTF-8. Unlike the Default encoding, the quotation mark is
        // encoded as \" rather than \u0022. Unlike the Default encoding (which only allows UnicodeRanges.BasicLatin,
        // the Basic Latin Unicode block from U+0021 to U+007F), using this encoder instance allows UnicodeRanges.All
        // (a range that consists of the entire Basic Multilingual Plane, from U+0000 to U+FFFF) to go through unescaped.
        // https://learn.microsoft.com/en-us/dotnet/api/system.text.encodings.web.javascriptencoder.unsaferelaxedjsonescaping?view=net-9.0
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    #endregion

    #region [PROPERTIES]
    public bool FirstRun
    {
        get => bool.Parse(Get(nameof(FirstRun)) ?? "True");
        set => Set(nameof(FirstRun), $"{value}");
    }

    public bool Logging
    {
        get => bool.Parse(Get(nameof(Logging)) ?? "False");
        set => Set(nameof(Logging), $"{value}");
    }

    public string CompatibleVersion
    {
        get => Get(nameof(CompatibleVersion));
        set => Set(nameof(CompatibleVersion), value);
    }

    public string Metrics
    {
        get => Get(nameof(Metrics));
        set => Set(nameof(Metrics), value);
    }

    public int RetryCount
    {
        get => int.Parse(Get(nameof(RetryCount)) ?? "3");
        set => Set(nameof(RetryCount), $"{value}");
    }

    public int PositionX
    {
        get => int.Parse(Get(nameof(PositionX)) ?? "100");
        set => Set(nameof(PositionX), $"{value}");
    }

    public int PositionY
    {
        get => int.Parse(Get(nameof(PositionY)) ?? "100");
        set => Set(nameof(PositionY), $"{value}");
    }

    public DateTime LastUse
    {
        get => DateTime.Parse(Get(nameof(LastUse)) ?? DateTime.MinValue.ToString());
        set => Set(nameof(LastUse), value.ToString());
    }

    public string User
    {
        get => Get(nameof(User));
        set => Set(nameof(User), value);
    }

    [EncryptedAttribute] // mark any prop that should be encrypted with this attribute
    public string APIKey
    {
        get => Get(nameof(APIKey));
        set => Set(nameof(APIKey), value);
    }
    #endregion

    /// <summary>
    /// By default the configuration file is named <c>settings.config</c> and will always be located in the application's base directory.
    /// </summary>
    /// <param name="fileName">config file name, not the full path</param>
    public LazyConfig(string fileName = "settings.config", IEncryptor encryptor = null)
    {
        _fileName = fileName;
        _encryptor = encryptor;
        _map = new Lazy<Dictionary<string, string>>(() => LoadSettings(), LazyThreadSafetyMode.ExecutionAndPublication);
        _encryptedKeys = DiscoverEncryptedKeys();
    }

    #region [GETTERS/SETTERS]
    string Get(string key)
    {
        var dict = _map.Value;
        if (!dict.TryGetValue(key, out var raw)) return null;

        if (_encryptor != null && _encryptedKeys.Contains(key) && !string.IsNullOrEmpty(raw))
        {
            try { return _encryptor.Decrypt(raw); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not decrypt '{key}'", ex);
            }
        }
        return raw;
    }

    /// <summary>
    /// Every time the setter is called it will update the value in the config file.
    /// </summary>
    void Set(string key, string value)
    {
        var dict = _map.Value;   // triggers lazy load
        if (_encryptor != null && _encryptedKeys.Contains(key) && value is not null)
            dict[key] = _encryptor.Encrypt(value);
        else
            dict[key] = value;
        SaveSettings(dict);
    }
    #endregion

    #region [I/O HELPERS]
    Dictionary<string, string> LoadSettings()
    {
        _ioLock.EnterReadLock();
        try
        {
            if (!File.Exists(_fileName))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_fileName);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsopts) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] Error reading config: {ex.Message}");
        }
        finally
        {
            _ioLock.ExitReadLock();
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    void SaveSettings(Dictionary<string, string> map)
    {
        _ioLock.EnterWriteLock();
        try
        {
            var json = JsonSerializer.Serialize(map, _jsopts);
                
            if (!string.IsNullOrEmpty(_parity) && _strComp.Equals(json, _parity))
                return; // no change, skip file I/O
                
            _parity = json; // update content parity

            File.WriteAllText(_fileName, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] Error saving config: {ex.Message}");
        }
        finally
        {
            _ioLock.ExitWriteLock();
        }
    }
    #endregion

    /// <summary>
    /// Use reflection to find all properties marked with <see cref="EncryptedAttribute"/>.
    /// </summary>
    /// <returns><see cref="HashSet{T}"/></returns>
    HashSet<string> DiscoverEncryptedKeys()
    {
        return new HashSet<string>(this.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<EncryptedAttribute>() != null)
                .Select(p => p.Name), StringComparer.OrdinalIgnoreCase
        );
    }
}

#region [ENCRYPTION SUPPORT]
[AttributeUsage(AttributeTargets.Property)]
public class EncryptedAttribute : Attribute { }

/// <summary>
/// Encryption contract
/// </summary>
public interface IEncryptor
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

/// <summary>
/// Portable AES encryption
/// </summary>
public class AesPortableEncryptor : IEncryptor
{
    readonly byte[] _key;

    /// <summary>
    /// <para>
    ///   Provides a portable <c>Advanced Encryption Standard</c> implementation so that configurations can be shared across machine domains.
    /// </para>
    /// <para>
    ///   Rfc2898DeriveBytes implements the PBKDF2 algorithm (Password-Based Key Derivation Function 2), 
    ///   defined in RFC 2898. PBKDF2 is a key derivation function that uses a pseudorandom function 
    ///   (like HMAC-SHA1) to repeatedly hash the password and salt, making it more resistant to 
    ///   brute-force attacks.
    /// </para>
    /// <para>
    ///   While passphrase length is important for security, the salt and iteration count are crucial for 
    ///   making brute-force attacks computationally expensive. The salt is a random value that is unique 
    ///   to each password, and the iteration count determines how many times the hashing process is repeated.
    /// </para>
    /// <list type="bullet">
    ///    <listheader>
    ///         <term>Usage</term><description>ancillary notes</description>
    ///     </listheader>
    ///     <item><description>Salt must be the same across machines to preserve portability.</description></item>
    ///     <item><description>Salt must be kept secret or obfuscated (or as configuration-only).</description></item>
    ///     <item><description>If not, tools such as <c>Ghidra</c> could be used to defeat the security.</description></item>
    /// </list>
    /// </summary>
    /// <param name="passphrase"></param>
    /// <param name="salt"></param>
    /// <param name="iterations"></param>
    /// <seealso cref="Encrypt(string)"/>
    /// <seealso cref="Decrypt(string)"/>
    public AesPortableEncryptor(string passphrase, byte[] salt, int iterations = 10000)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException(nameof(passphrase), "The passphrase cannot be null or empty.");
        if (passphrase.Length < 16)
            throw new ArgumentException(nameof(passphrase), "The passphrase should be no less than 16 characters.");
        if (salt.Length < 16)
            throw new ArgumentException(nameof(salt), "The salt should be no less than 16 bytes.");
        if (iterations < 10000)
            throw new ArgumentException(nameof(iterations), "The iteration count is too low, 10K minimum is recommended.");

        using (var derive = new Rfc2898DeriveBytes(passphrase, salt, iterations /*, HashAlgorithmName.SHA256 */))
        {
            _key = derive.GetBytes(32);  // 256-bit key
        }
    }

    /// <summary>
    /// Returns the given <paramref name="plainText"/> as an encrypted base64 string.
    /// </summary>
    public string Encrypt(string plainText)
    {
        try
        {
            using (var aes = Aes.Create())
            {
                aes.BlockSize = 128; // a 4x4 array containing 16 bytes
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _key;
                // generate random initialization vector
                aes.GenerateIV();
                using (var encryptor = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plainText);
                    var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    // prepend initialization vector to cipher text
                    var result = new byte[aes.IV.Length + cipherBytes.Length];
                    Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                    Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
                    return Convert.ToBase64String(result);
                }
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the given <paramref name="cipherText"/> as a decrypted string.
    /// </summary>
    public string Decrypt(string cipherText)
    {
        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);
                
            // extract IV
            var iv = new byte[16];
            Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);

            // extract cipher
            var cipherBytes = new byte[fullCipher.Length - iv.Length];
            Buffer.BlockCopy(fullCipher, iv.Length, cipherBytes, 0, cipherBytes.Length);

            using (var aes = Aes.Create())
            {
                aes.BlockSize = 128; // a 4x4 array containing 16 bytes
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _key;
                aes.IV = iv;
                using (var decryptor = aes.CreateDecryptor())
                {
                    var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
#endregion
