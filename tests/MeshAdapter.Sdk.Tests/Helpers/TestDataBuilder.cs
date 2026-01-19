using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Builder class for creating test data objects.
/// </summary>
public static class TestDataBuilder
{
    /// <summary>
    /// Creates a JObject representing an entity with the given properties.
    /// </summary>
    public static JObject CreateEntity(
        string? rtId = null,
        string? ckTypeId = null,
        string? wellKnownName = null,
        Dictionary<string, object>? attributes = null)
    {
        var entity = new JObject
        {
            ["RtId"] = rtId ?? Guid.NewGuid().ToString(),
            ["CkTypeId"] = ckTypeId ?? "TestType"
        };

        if (wellKnownName != null)
        {
            entity["RtWellKnownName"] = wellKnownName;
        }

        if (attributes != null)
        {
            foreach (var (key, value) in attributes)
            {
                entity[key] = JToken.FromObject(value);
            }
        }

        return entity;
    }

    /// <summary>
    /// Creates a JArray of entities.
    /// </summary>
    public static JArray CreateEntityArray(int count, string typePrefix = "Entity", string? ckTypeId = null)
    {
        var array = new JArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(CreateEntity(
                rtId: $"{typePrefix}-{i}",
                ckTypeId: ckTypeId ?? "TestType",
                attributes: new Dictionary<string, object>
                {
                    ["Name"] = $"{typePrefix} {i}",
                    ["Index"] = i
                }));
        }
        return array;
    }

    /// <summary>
    /// Creates a JObject with nested data structure for testing path resolution.
    /// </summary>
    public static JObject CreateNestedData(string rootProperty = "data")
    {
        return new JObject
        {
            [rootProperty] = new JObject
            {
                ["items"] = new JArray(
                    CreateEntity("item-1", attributes: new Dictionary<string, object> { ["Value"] = 100 }),
                    CreateEntity("item-2", attributes: new Dictionary<string, object> { ["Value"] = 200 }),
                    CreateEntity("item-3", attributes: new Dictionary<string, object> { ["Value"] = 300 })
                ),
                ["metadata"] = new JObject
                {
                    ["count"] = 3,
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                }
            }
        };
    }

    /// <summary>
    /// Creates test data for mapping tests.
    /// </summary>
    public static JObject CreateMappingTestData(string path, object value)
    {
        var data = new JObject();
        var pathParts = path.TrimStart('$', '.').Split('.');

        JObject current = data;
        for (var i = 0; i < pathParts.Length - 1; i++)
        {
            var newObj = new JObject();
            current[pathParts[i]] = newObj;
            current = newObj;
        }

        current[pathParts[^1]] = JToken.FromObject(value);
        return data;
    }

    /// <summary>
    /// Creates test data with placeholders for PlaceholderReplaceNode testing.
    /// </summary>
    public static JObject CreatePlaceholderTestData(string template, Dictionary<string, string> values)
    {
        var data = new JObject
        {
            ["template"] = template
        };

        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }

    /// <summary>
    /// Creates email test data.
    /// </summary>
    public static JObject CreateEmailTestData(
        string to = "test@example.com",
        string subject = "Test Subject",
        string body = "Test Body")
    {
        return new JObject
        {
            ["to"] = to,
            ["subject"] = subject,
            ["body"] = body
        };
    }

    /// <summary>
    /// Creates test data with multiple recipients.
    /// </summary>
    public static JObject CreateEmailTestDataWithMultipleRecipients(
        IEnumerable<string> toAddresses,
        string subject = "Test Subject",
        string body = "Test Body")
    {
        return new JObject
        {
            ["to"] = new JArray(toAddresses),
            ["subject"] = subject,
            ["body"] = body
        };
    }
}
