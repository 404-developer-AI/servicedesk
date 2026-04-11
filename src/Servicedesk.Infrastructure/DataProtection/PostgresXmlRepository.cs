using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Servicedesk.Infrastructure.DataProtection;

/// <summary>
/// ASP.NET Data Protection keyring persisted to PostgreSQL, encrypted at rest
/// with AES-GCM using a 32-byte master key from configuration. The plaintext
/// XML (which contains the symmetric keys the framework uses to sign cookies
/// and antiforgery tokens) never touches the database: each element is sealed
/// under a fresh 12-byte nonce and bound to its friendly name via AEAD
/// associated data, so a leaked DB backup cannot forge sessions.
/// </summary>
/// <remarks>
/// A leaked backup is the realistic threat here (copies end up on laptops,
/// S3, email). Anyone with live host access already owns the app. Master key
/// rotation means re-encrypting every row under the new key — handled out of
/// band by ops, not by this class.
/// </remarks>
public sealed class PostgresXmlRepository : IXmlRepository
{
    private const string SelectAllSql = """
        SELECT friendly_name, nonce, ciphertext, tag
        FROM data_protection_keys
        ORDER BY id ASC
        """;

    private const string InsertSql = """
        INSERT INTO data_protection_keys (friendly_name, nonce, ciphertext, tag)
        VALUES (@FriendlyName, @Nonce, @Ciphertext, @Tag)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly byte[] _masterKey;
    private readonly ILogger<PostgresXmlRepository> _logger;

    public PostgresXmlRepository(
        NpgsqlDataSource dataSource,
        byte[] masterKey,
        ILogger<PostgresXmlRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("Master key must be 32 bytes (AES-256).", nameof(masterKey));
        }

        _dataSource = dataSource;
        _masterKey = masterKey;
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var results = new List<XElement>();

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAllSql;
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var friendlyName = reader.GetString(0);
            var nonce = (byte[])reader.GetValue(1);
            var ciphertext = (byte[])reader.GetValue(2);
            var tag = (byte[])reader.GetValue(3);

            try
            {
                var xml = Decrypt(nonce, ciphertext, tag, friendlyName);
                results.Add(XElement.Parse(xml));
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex,
                    "Failed to decrypt data protection key {FriendlyName}. " +
                    "Master key may have rotated without re-encrypting existing rows.",
                    friendlyName);
                throw;
            }
        }

        _logger.LogInformation("Loaded {Count} data protection keys from Postgres.", results.Count);
        return results;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(friendlyName);

        var (nonce, ciphertext, tag) = Encrypt(element.ToString(SaveOptions.DisableFormatting), friendlyName);

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        command.Parameters.Add(new NpgsqlParameter("@FriendlyName", NpgsqlDbType.Text) { Value = friendlyName });
        command.Parameters.Add(new NpgsqlParameter("@Nonce", NpgsqlDbType.Bytea) { Value = nonce });
        command.Parameters.Add(new NpgsqlParameter("@Ciphertext", NpgsqlDbType.Bytea) { Value = ciphertext });
        command.Parameters.Add(new NpgsqlParameter("@Tag", NpgsqlDbType.Bytea) { Value = tag });
        command.ExecuteNonQuery();

        _logger.LogInformation("Stored data protection key {FriendlyName}.", friendlyName);
    }

    private (byte[] nonce, byte[] ciphertext, byte[] tag) Encrypt(string plaintext, string friendlyName)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var associatedData = Encoding.UTF8.GetBytes(friendlyName);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintextBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_masterKey, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);

        return (nonce, ciphertext, tag);
    }

    private string Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, string friendlyName)
    {
        var associatedData = Encoding.UTF8.GetBytes(friendlyName);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_masterKey, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return Encoding.UTF8.GetString(plaintext);
    }
}
