using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Builder class for creating test data objects.
/// </summary>
public static class TestDataBuilder
{
    /// <summary>
    /// Creates a JsonObject representing an entity with the given properties.
    /// </summary>
    public static JsonObject CreateEntity(
        string? rtId = null,
        string? ckTypeId = null,
        string? wellKnownName = null,
        Dictionary<string, object>? attributes = null)
    {
        var entity = new JsonObject
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
                entity[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        return entity;
    }

    /// <summary>
    /// Creates a JsonArray of entities.
    /// </summary>
    public static JsonArray CreateEntityArray(int count, string typePrefix = "Entity", string? ckTypeId = null)
    {
        var array = new JsonArray();
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
    /// Creates a JsonObject with nested data structure for testing path resolution.
    /// </summary>
    public static JsonObject CreateNestedData(string rootProperty = "data")
    {
        return new JsonObject
        {
            [rootProperty] = new JsonObject
            {
                ["items"] = new JsonArray(
                    CreateEntity("item-1", attributes: new Dictionary<string, object> { ["Value"] = 100 }),
                    CreateEntity("item-2", attributes: new Dictionary<string, object> { ["Value"] = 200 }),
                    CreateEntity("item-3", attributes: new Dictionary<string, object> { ["Value"] = 300 })
                ),
                ["metadata"] = new JsonObject
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
    public static JsonObject CreateMappingTestData(string path, object value)
    {
        var data = new JsonObject();
        var pathParts = path.TrimStart('$', '.').Split('.');

        JsonObject current = data;
        for (var i = 0; i < pathParts.Length - 1; i++)
        {
            var newObj = new JsonObject();
            current[pathParts[i]] = newObj;
            current = newObj;
        }

        current[pathParts[^1]] = JsonSerializer.SerializeToNode(value);
        return data;
    }

    /// <summary>
    /// Creates test data with placeholders for PlaceholderReplaceNode testing.
    /// </summary>
    public static JsonObject CreatePlaceholderTestData(string template, Dictionary<string, string> values)
    {
        var data = new JsonObject
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
    public static JsonObject CreateEmailTestData(
        string to = "test@example.com",
        string subject = "Test Subject",
        string body = "Test Body")
    {
        return new JsonObject
        {
            ["to"] = to,
            ["subject"] = subject,
            ["body"] = body
        };
    }

    /// <summary>
    /// Creates test data with multiple recipients.
    /// </summary>
    public static JsonObject CreateEmailTestDataWithMultipleRecipients(
        IEnumerable<string> toAddresses,
        string subject = "Test Subject",
        string body = "Test Body")
    {
        var arr = new JsonArray();
        foreach (var to in toAddresses)
        {
            arr.Add(to);
        }
        return new JsonObject
        {
            ["to"] = arr,
            ["subject"] = subject,
            ["body"] = body
        };
    }
}
