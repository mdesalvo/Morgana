using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
using System.Security.Cryptography;
using System.Text.Json;

namespace Morgana.Framework.Services;

/// <summary>
/// File-based conversation persistence service with AES encryption.
/// Stores each conversation as an encrypted {conversationId}.morgana.json file on disk.
/// </summary>
/// <remarks>
/// <para><strong>Storage Model:</strong></para>
/// <para>Each conversation is stored as a separate encrypted file following the pattern:</para>
/// <code>
/// {StoragePath}/{conversationId}.morgana.json
///
/// Example:
/// C:/MorganaData/conv_12345.morgana.json
/// C:/MorganaData/conv_67890.morgana.json
/// </code>
/// <para><strong>Encryption:</strong></para>
/// <para>Files are encrypted using AES-256 in CBC mode with PKCS7 padding.
/// The encryption key and IV are derived from the configured EncryptionKey in appsettings.json.
/// This provides protection for conversations at rest.</para>
/// <para><strong>Configuration:</strong></para>
/// <code>
/// // appsettings.json
/// {
///   "Morgana": {
///     "ConversationPersistence": {
///       "StoragePath": "C:/MorganaData",
///       "EncryptionKey": "your-base64-encoded-256-bit-key"
///     }
///   }
/// }
/// </code>
/// <para><strong>Thread Safety:</strong></para>
/// <para>Uses file system locking for thread safety. Concurrent writes to the same conversation
/// use last-write-wins semantics. Reads are non-blocking.</para>
/// <para><strong>Performance Characteristics:</strong></para>
/// <list type="bullet">
/// <item>Save: O(1) - single file write with encryption</item>
/// <item>Load: O(1) - single file read with decryption</item>
/// <item>Exists: O(1) - file existence check</item>
/// <item>Delete: O(1) - single file delete</item>
/// </list>
/// </remarks>
public class EncryptedFileConversationPersistenceService : IConversationPersistenceService
{
    private readonly ILogger<EncryptedFileConversationPersistenceService> logger;
    private readonly Records.ConversationPersistenceOptions options;
    private readonly byte[] encryptionKey;

    /// <summary>
    /// Initializes a new instance of the EncryptedFileConversationPersistenceService.
    /// </summary>
    /// <param name="options">Configuration options containing storage path and encryption key</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    /// <exception cref="ArgumentException">Thrown if StoragePath or EncryptionKey are not configured</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown if StoragePath does not exist and cannot be created</exception>
    public EncryptedFileConversationPersistenceService(
        IOptions<Records.ConversationPersistenceOptions> options,
        ILogger<EncryptedFileConversationPersistenceService> logger)
    {
        this.logger = logger;
        this.options = options.Value;

        if (string.IsNullOrWhiteSpace(this.options.StoragePath))
            throw new ArgumentException("StoragePath must be configured in appsettings.json");

        if (string.IsNullOrWhiteSpace(this.options.EncryptionKey))
            throw new ArgumentException("EncryptionKey must be configured in appsettings.json");

        // Ensure storage directory exists
        Directory.CreateDirectory(this.options.StoragePath);

        // Derive encryption key from configured key
        encryptionKey = Convert.FromBase64String(this.options.EncryptionKey);

        if (encryptionKey.Length != 32)
            throw new ArgumentException("EncryptionKey must be a 256-bit (32-byte) key encoded as Base64");

        logger.LogInformation($"{nameof(EncryptedFileConversationPersistenceService)} initialized with storage path: {this.options.StoragePath}");
    }

    /// <inheritdoc/>
    public async Task SaveConversationAsync(
        string conversationId,
        AgentThread thread,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        string filePath = GetFilePath(conversationId);
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // Serialize AgentThread to JSON
            JsonElement serialized = thread.Serialize(jsonSerializerOptions);
            string json = JsonSerializer.Serialize(serialized, jsonSerializerOptions);

            // Encrypt JSON content
            byte[] encryptedData = Encrypt(json);

            // Write to file atomically (write to temp file, then replace)
            string tempPath = $"{filePath}.tmp";
            await File.WriteAllBytesAsync(tempPath, encryptedData);
            File.Move(tempPath, filePath, overwrite: true);

            logger.LogInformation($"Saved conversation {conversationId} to {filePath} ({encryptedData.Length} bytes encrypted)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to save conversation {conversationId}");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<AgentThread?> LoadConversationAsync(
        string conversationId,
        MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        string filePath = GetFilePath(conversationId);
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        if (!File.Exists(filePath))
        {
            logger.LogInformation($"Conversation {conversationId} not found, returning null");
            return null;
        }

        try
        {
            // Read encrypted file
            byte[] encryptedData = await File.ReadAllBytesAsync(filePath);

            // Decrypt content
            string json = Decrypt(encryptedData);

            // Deserialize JSON to JsonElement
            JsonElement serialized = JsonSerializer.Deserialize<JsonElement>(json, jsonSerializerOptions);

            // Deserialize thread via agent (which handles context provider reconstruction)
            AgentThread restored = agent.DeserializeThread(serialized, jsonSerializerOptions);

            logger.LogInformation($"Loaded conversation {conversationId} from {filePath} ({encryptedData.Length} bytes decrypted)");

            return restored;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to load conversation {conversationId}");
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<bool> ConversationExistsAsync(string conversationId)
    {
        string filePath = GetFilePath(conversationId);
        bool exists = File.Exists(filePath);

        logger.LogDebug($"Conversation {conversationId} exists: {exists}");

        return Task.FromResult(exists);
    }

    /// <inheritdoc/>
    public Task DeleteConversationAsync(string conversationId)
    {
        string filePath = GetFilePath(conversationId);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            logger.LogInformation($"Deleted conversation {conversationId} from {filePath}");
        }
        else
        {
            logger.LogDebug($"Conversation {conversationId} does not exist, nothing to delete");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Constructs the full file path for a conversation.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>Full path to the .morgana.json file</returns>
    private string GetFilePath(string conversationId)
    {
        // Sanitize conversationId to prevent directory traversal attacks
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(options.StoragePath, $"{sanitized}.morgana.json");
    }

    /// <summary>
    /// Encrypts plaintext JSON using AES-256-CBC.
    /// </summary>
    /// <param name="plaintext">JSON string to encrypt</param>
    /// <returns>Encrypted bytes (IV prepended to ciphertext)</returns>
    private byte[] Encrypt(string plaintext)
    {
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.GenerateIV();

        using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream msEncrypt = new MemoryStream();

        // Write IV to beginning of stream (needed for decryption)
        msEncrypt.Write(aes.IV, 0, aes.IV.Length);

        using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(plaintext);
        }

        return msEncrypt.ToArray();
    }

    /// <summary>
    /// Decrypts encrypted bytes using AES-256-CBC.
    /// </summary>
    /// <param name="ciphertext">Encrypted bytes (IV prepended to ciphertext)</param>
    /// <returns>Decrypted JSON string</returns>
    private string Decrypt(byte[] ciphertext)
    {
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;

        // Extract IV from beginning of ciphertext
        byte[] iv = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream msDecrypt = new MemoryStream(ciphertext, iv.Length, ciphertext.Length - iv.Length);
        using CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new StreamReader(csDecrypt);

        return srDecrypt.ReadToEnd();
    }
}