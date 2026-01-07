using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

public interface IRedisHash
{
    Task<bool> SetAsync<T>(
        string key,
        string field,
        T value,
        TimeSpan? keyTtl = null,
        When when = When.Always,
        CommandFlags flags = CommandFlags.None);

    Task<T?> GetAsync<T>(
        string key,
        string field,
        CommandFlags flags = CommandFlags.None);

    Task<Dictionary<string, T?>> GetManyAsync<T>(
        string key,
        IEnumerable<string> fields,
        CommandFlags flags = CommandFlags.None);

    Task<long> SetManyAsync<T>(
        string key,
        IReadOnlyDictionary<string, T> values,
        TimeSpan? keyTtl = null,
        CommandFlags flags = CommandFlags.None);

    Task<bool> FieldExistsAsync(
        string key,
        string field,
        CommandFlags flags = CommandFlags.None);

    Task<bool> DeleteFieldAsync(
        string key,
        string field,
        CommandFlags flags = CommandFlags.None);

    Task<long> DeleteFieldsAsync(
        string key,
        IEnumerable<string> fields,
        CommandFlags flags = CommandFlags.None);

    Task<double> IncrementAsync(
        string key,
        string field,
        double by = 1,
        CommandFlags flags = CommandFlags.None);

    Task<Dictionary<string, string>> GetAllRawAsync(
        string key,
        CommandFlags flags = CommandFlags.None);
}

public sealed class RedisHash : IRedisHash
{
    private readonly IDatabase _db;
    private readonly JsonSerializerOptions _json;

    public RedisHash(IConnectionMultiplexer mux, int db = -1, JsonSerializerOptions? jsonOptions = null)
    {
        if (mux is null) throw new ArgumentNullException(nameof(mux));
        _db = mux.GetDatabase(db);
        _json = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<bool> SetAsync<T>(
        string key,
        string field,
        T value,
        TimeSpan? keyTtl = null,
        When when = When.Always,
        CommandFlags flags = CommandFlags.None)
    {
        ValidateKeyField(key, field);

        RedisValue payload = Serialize(value);

        // HashSet returns true only when a new field was created.
        bool created = await _db.HashSetAsync(key, field, payload, when, flags).ConfigureAwait(false);

        if (keyTtl.HasValue)
        {
            // TTL applies to the entire hash key.
            await _db.KeyExpireAsync(key, keyTtl, flags).ConfigureAwait(false);
        }

        return created;
    }

    public async Task<T?> GetAsync<T>(string key, string field, CommandFlags flags = CommandFlags.None)
    {
        ValidateKeyField(key, field);

        RedisValue v = await _db.HashGetAsync(key, field, flags).ConfigureAwait(false);
        if (v.IsNull) return default;

        return Deserialize<T>(v);
    }

    public async Task<Dictionary<string, T?>> GetManyAsync<T>(
        string key,
        IEnumerable<string> fields,
        CommandFlags flags = CommandFlags.None)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        var list = fields.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToArray();
        if (list.Length == 0) return new Dictionary<string, T?>();

        RedisValue[] redisFields = list.Select(f => (RedisValue)f).ToArray();
        RedisValue[] values = await _db.HashGetAsync(key, redisFields, flags).ConfigureAwait(false);

        var result = new Dictionary<string, T?>(list.Length, StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
        {
            result[list[i]] = values[i].IsNull ? default : Deserialize<T>(values[i]);
        }
        return result;
    }

    public async Task<long> SetManyAsync<T>(
        string key,
        IReadOnlyDictionary<string, T> values,
        TimeSpan? keyTtl = null,
        CommandFlags flags = CommandFlags.None)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (values.Count == 0) return 0;

        HashEntry[] entries = values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => new HashEntry(kv.Key, Serialize(kv.Value)))
            .ToArray();

        if (entries.Length == 0) return 0;

        await _db.HashSetAsync(key, entries, flags).ConfigureAwait(false);

        if (keyTtl.HasValue)
        {
            await _db.KeyExpireAsync(key, keyTtl, flags).ConfigureAwait(false);
        }

        return entries.LongLength;
    }

    public Task<bool> FieldExistsAsync(string key, string field, CommandFlags flags = CommandFlags.None)
    {
        ValidateKeyField(key, field);
        return _db.HashExistsAsync(key, field, flags);
    }

    public Task<bool> DeleteFieldAsync(string key, string field, CommandFlags flags = CommandFlags.None)
    {
        ValidateKeyField(key, field);
        return _db.HashDeleteAsync(key, field, flags);
    }

    public async Task<long> DeleteFieldsAsync(string key, IEnumerable<string> fields, CommandFlags flags = CommandFlags.None)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        RedisValue[] redisFields = fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct()
            .Select(f => (RedisValue)f)
            .ToArray();

        if (redisFields.Length == 0) return 0;

        return await _db.HashDeleteAsync(key, redisFields, flags).ConfigureAwait(false);
    }

    public Task<double> IncrementAsync(string key, string field, double by = 1, CommandFlags flags = CommandFlags.None)
    {
        ValidateKeyField(key, field);
        return _db.HashIncrementAsync(key, field, by, flags);
    }

    public async Task<Dictionary<string, string>> GetAllRawAsync(string key, CommandFlags flags = CommandFlags.None)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));

        HashEntry[] entries = await _db.HashGetAllAsync(key, flags).ConfigureAwait(false);

        var result = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            result[e.Name!] = e.Value!;
        }
        return result;
    }

    private void ValidateKeyField(string key, string field)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("Field is required.", nameof(field));
    }

    private RedisValue Serialize<T>(T value)
    {
        if (value is null) return RedisValue.Null;

        // If user passes string, store it as-is (avoid double-JSON quoting).
        if (value is string s) return s;

        return JsonSerializer.Serialize(value, _json);
    }

    private T? Deserialize<T>(RedisValue value)
    {
        // If caller expects string, return raw.
        if (typeof(T) == typeof(string))
            return (T)(object)value.ToString();

        return JsonSerializer.Deserialize<T>(value.ToString(), _json);
    }
}
