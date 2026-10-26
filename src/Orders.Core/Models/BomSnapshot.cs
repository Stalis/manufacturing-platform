using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Manufacturing.Core.Models;

namespace Orders.Core.Models;

public class BomSnapshot
{
    private string _hash = string.Empty;
    private string _json = string.Empty;
    private BillOfMaterial? _billOfMaterial;

    public BomSnapshot()
    {
    }

    public string Hash
    {
        get
        {
            if (!string.IsNullOrEmpty(_hash)) return _hash;
            if (string.IsNullOrEmpty(_json) && _billOfMaterial is not null)
            {
                _json = SerializeCanonical(_billOfMaterial);
            }

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(_json));
            _hash = Convert.ToHexString(hashBytes);
            return _hash;
        }
        private set => _hash = value;
    }

    public required BillOfMaterial BillOfMaterial
    {
        get => _billOfMaterial ??= JsonSerializer.Deserialize<BillOfMaterial>(_json)
            ?? throw new InvalidOperationException("Failed to deserialize BillOfMaterial from JSON.");
        init
        {
            _billOfMaterial = value;
            if (string.IsNullOrEmpty(_json))
            {
                _json = SerializeCanonical(value);
            }
        }
    }

    public string Json
    {
        get
        {
            if (string.IsNullOrEmpty(_json) && _billOfMaterial is not null)
            {
                _json = SerializeCanonical(_billOfMaterial);
            }

            return _json;
        }
        private set => _json = value;
    }

    public static BomSnapshot FromJson(string json)
    {
        var bom = JsonSerializer.Deserialize<BillOfMaterial>(json) ??
                  throw new InvalidOperationException("Failed to deserialize BillOfMaterial from JSON.");

        return new BomSnapshot
        {
            BillOfMaterial = bom,
            Json = SerializeCanonical(bom),
        };
    }

    private static string SerializeCanonical(BillOfMaterial bom)
    {
        var canonical = new BillOfMaterial
        {
            ProductId = bom.ProductId,
            Revision = bom.Revision,
            Materials = bom.Materials.OrderBy(m => m.MaterialId).ToList(),
        };
        return JsonSerializer.Serialize(canonical);
    }
}
