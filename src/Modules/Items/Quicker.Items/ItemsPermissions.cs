using Quicker.Identity.Contracts;

namespace Quicker.Items;

/// <summary>Permission keys of the item master; they live under the inventory area the role templates already grant.</summary>
public static class ItemsPermissions
{
    public const string ItemRead = "inventory.item.read";
    public const string ItemManage = "inventory.item.manage";
    public const string BomRead = "inventory.bom.read";
    public const string BomManage = "inventory.bom.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(ItemRead, "inventory", "Read items, variants, units, barcodes, suppliers, settings, categories, brands, attributes and substitutes"),
        new(ItemManage, "inventory", "Create, edit and import items, variants, units, barcodes, suppliers, settings, categories, brands, attributes and substitutes"),
        new(BomRead, "inventory", "Read bills of material and explode them"),
        new(BomManage, "inventory", "Create, edit and activate bills of material"),
    ];
}
