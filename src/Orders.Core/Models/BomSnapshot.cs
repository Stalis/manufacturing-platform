using Manufacturing.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Core.Models;

public class BomSnapshot
{
    private string _hash = string.Empty;
    public string Hash
    {
        get
        {
            if (!string.IsNullOrEmpty(_hash))
            {
                return _hash;
            }
            if (string.IsNullOrEmpty(Json))
            {
                Json = JsonSerializer.Serialize(BillOfMaterial);
            }

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Json));
            _hash = Convert.ToHexString(hashBytes);
            return _hash;
        }
    }

    public required BillOfMaterial BillOfMaterial { get; init; }
    public string Json { get; private set; } = string.Empty;

    public static BomSnapshot FromJson(string json)
    {
        var bom = JsonSerializer.Deserialize<BillOfMaterial>(json) ??
             throw new InvalidOperationException("Failed to deserialize BillOfMaterial from JSON.");

        return new BomSnapshot
        {
            BillOfMaterial = bom,
            Json = json
        };
    }
}
