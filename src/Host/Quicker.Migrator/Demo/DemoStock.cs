using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Application;
using Quicker.Accounting.Persistence;
using Quicker.Integrity.Contracts;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Persistence;
using Quicker.Items.Application;
using Quicker.Items.Persistence;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Organization.Persistence;
using Quicker.Persistence;

namespace Quicker.Migrator.Demo;

/// <summary>What the stock seed produced: warehouses (stocked and in transit), items, variants, lots, serials and opening-stock lines.</summary>
public sealed record DemoStockOutcome(int Warehouses, int Items, int Variants, int Lots, int Serials, int StockLines);

/// <summary>
/// Demo seed v3 (roadmap 3.9): the item master and the stock. Twenty-two product families across food, home,
/// electronics, apparel, pharmacy and industrial supplies make 5,000 items with bilingual names, brands, a category
/// tree, units and barcodes; apparel carries size and colour variants, pharmacy and fresh food are lot-tracked with
/// expiry (FEFO), electronics are serialised. Eight stocked warehouses (plus one in-transit per company) receive opening
/// stock through the stock posting engine, so every unit is costed and booked, and the invariant harness must pass
/// before the seed commits. Everything derives from the item's ordinal, so the dataset is the same on every run.
/// </summary>
internal static class DemoStock
{
    private sealed record Text(string En, string Ar);

    private sealed record Family(
        string Code,
        string Category,
        string Company,
        string Tracking,
        string Uom,
        decimal MinUsd,
        decimal MaxUsd,
        int MinQty,
        int MaxQty,
        int Weight,
        Text[] Products,
        Text[] Packs,
        bool Variants = false,
        int ShelfLifeDays = 0,
        bool Cold = false,
        string Supplier = "supp:jebel-ali");

    private sealed record Warehouse(string Code, string Company, string Branch, Text Name, string Kind = "standard", bool Bins = false, bool Cold = false);

    private sealed record StockedWarehouse(Warehouse Definition, Guid Id, Guid CompanyId, IReadOnlyList<Guid> Bins);

    private sealed record Line(Guid CompanyId, string Warehouse, StockLine Stock);

    private static readonly RoundingPolicy Rounding = RoundingPolicy.Default;

    private static readonly (string Code, string? Parent, Text Name)[] Categories =
    [
        ("FOOD", null, new("Food and beverages", "الأغذية والمشروبات")), ("BEV", "FOOD", new("Beverages", "المشروبات")), ("DAIRY", "FOOD", new("Dairy", "الألبان")), ("SNACK", "FOOD", new("Snacks", "الوجبات الخفيفة")), ("GROC", "FOOD", new("Groceries", "البقالة")),
        ("HOME", null, new("Home care", "العناية بالمنزل")), ("CLEAN", "HOME", new("Cleaning", "التنظيف")), ("PCARE", "HOME", new("Personal care", "العناية الشخصية")), ("KITCH", "HOME", new("Kitchenware", "أدوات المطبخ")),
        ("ELEC", null, new("Electronics", "الإلكترونيات")), ("PHONE", "ELEC", new("Phones and tablets", "الهواتف والأجهزة اللوحية")), ("NET", "ELEC", new("Networking", "الشبكات")), ("APPL", "ELEC", new("Appliances", "الأجهزة المنزلية")), ("ACC", "ELEC", new("Accessories", "الملحقات")),
        ("APP", null, new("Apparel", "الملابس")), ("MEN", "APP", new("Men", "رجالي")), ("WOMEN", "APP", new("Women", "نسائي")), ("KIDS", "APP", new("Kids", "أطفال")),
        ("PHARM", null, new("Pharmacy", "الصيدلية")), ("OTC", "PHARM", new("Medicines", "الأدوية")), ("VIT", "PHARM", new("Vitamins", "الفيتامينات")), ("MED", "PHARM", new("Medical supplies", "المستلزمات الطبية")), ("MEDDEV", "PHARM", new("Medical devices", "الأجهزة الطبية")),
        ("IND", null, new("Industrial", "الصناعية")), ("PACK", "IND", new("Packaging", "التغليف")), ("SPARE", "IND", new("Spare parts", "قطع الغيار")), ("BUILD", "IND", new("Building materials", "مواد البناء")), ("STAT", "IND", new("Stationery", "القرطاسية")),
    ];

    private static readonly Text[] Brands =
    [
        new("Rafidain", "الرافدين"), new("Tigris", "دجلة"), new("Al-Noor", "النور"), new("Babylon", "بابل"), new("Sumer", "سومر"), new("Basra Gold", "ذهب البصرة"), new("Ninawa", "نينوى"), new("Furat", "الفرات"),
        new("Karrada", "الكرادة"), new("Mesopotamia", "بلاد الرافدين"), new("Erbil Fresh", "أربيل الطازجة"), new("Gulf Star", "نجمة الخليج"), new("Palm", "النخيل"), new("Anbar", "الأنبار"), new("Zawraa", "الزوراء"), new("Sindbad", "السندباد"),
    ];

    private static readonly (string Code, Text Name, (string Code, Text Name)[] Values)[] Attributes =
    [
        ("SIZE", new("Size", "المقاس"), [("XS", new("XS", "XS")), ("S", new("S", "S")), ("M", new("M", "M")), ("L", new("L", "L")), ("XL", new("XL", "XL")), ("XXL", new("XXL", "XXL"))]),
        ("COLOUR", new("Colour", "اللون"), [("BLK", new("Black", "أسود")), ("WHT", new("White", "أبيض")), ("NVY", new("Navy", "كحلي")), ("RED", new("Red", "أحمر")), ("GRY", new("Grey", "رمادي")), ("BLU", new("Blue", "أزرق"))]),
    ];

    private static readonly Family[] Families =
    [
        new("BEV", "BEV", "IQT", "none", "PCS", 0.2m, 2.5m, 120, 4800, 12, [new("Water", "ماء"), new("Orange juice", "عصير برتقال"), new("Apple juice", "عصير تفاح"), new("Cola", "كولا"), new("Energy drink", "مشروب طاقة"), new("Sparkling water", "ماء غازي"), new("Iced tea", "شاي مثلج"), new("Mango nectar", "نكتار مانجو"), new("Lemonade", "ليمونادة")], [new("330ml", "٣٣٠ مل"), new("500ml", "٥٠٠ مل"), new("1L", "١ لتر"), new("1.5L", "١٫٥ لتر"), new("2L", "٢ لتر")], Supplier: "supp:turkish-foods"),
        new("DAIRY", "DAIRY", "IQT", "lot", "PCS", 0.4m, 6m, 24, 600, 8, [new("Milk", "حليب"), new("Yogurt", "لبن"), new("Cheese", "جبن"), new("Butter", "زبدة"), new("Cream", "قشطة"), new("Labneh", "لبنة"), new("Ayran", "عيران")], [new("200g", "٢٠٠ غ"), new("500g", "٥٠٠ غ"), new("1kg", "١ كغ"), new("1L", "١ لتر")], ShelfLifeDays: 30, Cold: true, Supplier: "supp:turkish-foods"),
        new("SNACK", "SNACK", "IQT", "lot", "PCS", 0.2m, 4m, 48, 2400, 10, [new("Potato chips", "رقائق بطاطا"), new("Biscuits", "بسكويت"), new("Chocolate", "شوكولاتة"), new("Wafers", "ويفر"), new("Mixed nuts", "مكسرات مشكلة"), new("Dates", "تمر"), new("Crackers", "مقرمشات")], [new("40g", "٤٠ غ"), new("100g", "١٠٠ غ"), new("250g", "٢٥٠ غ"), new("500g", "٥٠٠ غ")], ShelfLifeDays: 270, Supplier: "supp:turkish-foods"),
        new("GROC", "GROC", "IQT", "lot", "PCS", 0.5m, 12m, 50, 2000, 12, [new("Rice", "رز"), new("Sugar", "سكر"), new("Flour", "طحين"), new("Cooking oil", "زيت طعام"), new("Tea", "شاي"), new("Coffee", "قهوة"), new("Lentils", "عدس"), new("Chickpeas", "حمص"), new("Tomato paste", "معجون طماطم"), new("Pasta", "معكرونة")], [new("500g", "٥٠٠ غ"), new("1kg", "١ كغ"), new("2kg", "٢ كغ"), new("5kg", "٥ كغ"), new("10kg", "١٠ كغ")], ShelfLifeDays: 365, Supplier: "supp:turkish-foods"),
        new("CLEAN", "CLEAN", "IQT", "none", "PCS", 0.5m, 8m, 24, 1200, 8, [new("Dish soap", "سائل جلي"), new("Laundry detergent", "مسحوق غسيل"), new("Bleach", "مبيض"), new("Floor cleaner", "منظف أرضيات"), new("Sponges", "إسفنج"), new("Trash bags", "أكياس قمامة"), new("Glass cleaner", "منظف زجاج")], [new("500ml", "٥٠٠ مل"), new("1L", "١ لتر"), new("3kg", "٣ كغ"), new("5L", "٥ لتر"), new("Pack of 10", "عبوة ١٠")]),
        new("PCARE", "PCARE", "IQT", "lot", "PCS", 0.8m, 15m, 24, 800, 7, [new("Shampoo", "شامبو"), new("Soap", "صابون"), new("Toothpaste", "معجون أسنان"), new("Deodorant", "مزيل عرق"), new("Body lotion", "لوشن جسم"), new("Razors", "شفرات حلاقة"), new("Diapers", "حفاضات")], [new("100ml", "١٠٠ مل"), new("250ml", "٢٥٠ مل"), new("400ml", "٤٠٠ مل"), new("Pack of 3", "عبوة ٣"), new("Pack of 40", "عبوة ٤٠")], ShelfLifeDays: 720),
        new("KITCH", "KITCH", "AEG", "none", "PCS", 2m, 60m, 6, 300, 4, [new("Frying pan", "مقلاة"), new("Saucepan", "قدر"), new("Knife set", "طقم سكاكين"), new("Glass set", "طقم أكواب"), new("Plate set", "طقم صحون"), new("Kettle", "غلاية"), new("Storage box", "صندوق تخزين")], [new("20cm", "٢٠ سم"), new("24cm", "٢٤ سم"), new("28cm", "٢٨ سم"), new("6 pcs", "٦ قطع"), new("12 pcs", "١٢ قطعة")]),
        new("PHONE", "PHONE", "USI", "serial", "PCS", 90m, 900m, 1, 5, 3, [new("Smartphone", "هاتف ذكي"), new("Feature phone", "هاتف عادي"), new("Tablet", "جهاز لوحي")], [new("64GB", "٦٤ غ.ب"), new("128GB", "١٢٨ غ.ب"), new("256GB", "٢٥٦ غ.ب"), new("512GB", "٥١٢ غ.ب")]),
        new("NET", "NET", "USI", "serial", "PCS", 25m, 450m, 1, 5, 3, [new("Router", "راوتر"), new("8-port switch", "محوّل ٨ منافذ"), new("24-port switch", "محوّل ٢٤ منفذًا"), new("Access point", "نقطة وصول"), new("Modem", "مودم"), new("Firewall", "جدار حماية")], [new("Wi-Fi 5", "واي فاي ٥"), new("Wi-Fi 6", "واي فاي ٦"), new("Gigabit", "غيغابت"), new("PoE", "PoE")]),
        new("APPL", "APPL", "USI", "serial", "PCS", 60m, 1200m, 1, 4, 3, [new("Refrigerator", "ثلاجة"), new("Washing machine", "غسالة"), new("Air conditioner", "مكيف"), new("Microwave", "مايكروويف"), new("Television", "تلفاز"), new("Water heater", "سخان ماء"), new("Vacuum cleaner", "مكنسة كهربائية")], [new("Compact", "صغير"), new("Standard", "قياسي"), new("Large", "كبير")]),
        new("ACC", "ACC", "USI", "none", "PCS", 1m, 40m, 24, 1200, 8, [new("Charger", "شاحن"), new("USB cable", "كابل USB"), new("Earphones", "سماعات أذن"), new("Power bank", "بطارية متنقلة"), new("Phone case", "غطاء هاتف"), new("Screen protector", "واقي شاشة"), new("Memory card", "بطاقة ذاكرة")], [new("1m", "١ م"), new("2m", "٢ م"), new("10000mAh", "١٠٠٠٠ مللي أمبير"), new("20000mAh", "٢٠٠٠٠ مللي أمبير"), new("32GB", "٣٢ غ.ب"), new("64GB", "٦٤ غ.ب")]),
        new("MEN", "MEN", "AEG", "none", "PCS", 4m, 45m, 12, 400, 6, [new("T-shirt", "تيشيرت"), new("Polo shirt", "قميص بولو"), new("Jeans", "جينز"), new("Jacket", "سترة"), new("Dishdasha", "دشداشة"), new("Trousers", "بنطلون"), new("Hoodie", "هودي")], [new("Cotton", "قطن"), new("Linen", "كتان"), new("Denim", "دنيم"), new("Wool", "صوف")], Variants: true),
        new("WOMEN", "WOMEN", "AEG", "none", "PCS", 5m, 60m, 12, 400, 5, [new("Abaya", "عباية"), new("Blouse", "بلوزة"), new("Dress", "فستان"), new("Scarf", "وشاح"), new("Cardigan", "كارديغان"), new("Skirt", "تنورة")], [new("Cotton", "قطن"), new("Silk", "حرير"), new("Chiffon", "شيفون"), new("Linen", "كتان")], Variants: true),
        new("KIDS", "KIDS", "AEG", "none", "PCS", 3m, 30m, 12, 500, 4, [new("Kids T-shirt", "تيشيرت أطفال"), new("School uniform", "زي مدرسي"), new("Pyjamas", "بيجاما"), new("Kids jacket", "سترة أطفال"), new("Socks pack", "طقم جوارب")], [new("Cotton", "قطن"), new("Fleece", "فليس"), new("Polyester", "بوليستر")], Variants: true),
        new("OTC", "OTC", "IQT", "lot", "PCS", 0.5m, 12m, 50, 2000, 6, [new("Paracetamol 500mg", "باراسيتامول ٥٠٠ ملغ"), new("Ibuprofen 400mg", "إيبوبروفين ٤٠٠ ملغ"), new("Cough syrup", "شراب سعال"), new("Antacid", "مضاد حموضة"), new("Oral rehydration salts", "أملاح معالجة الجفاف"), new("Nasal spray", "بخاخ أنف"), new("Eye drops", "قطرة عين")], [new("10 tablets", "١٠ أقراص"), new("20 tablets", "٢٠ قرصًا"), new("100ml", "١٠٠ مل"), new("30 sachets", "٣٠ كيسًا")], ShelfLifeDays: 730),
        new("VIT", "VIT", "IQT", "lot", "PCS", 2m, 25m, 24, 800, 4, [new("Vitamin C", "فيتامين ج"), new("Vitamin D3", "فيتامين د٣"), new("Multivitamin", "فيتامينات متعددة"), new("Omega-3", "أوميغا ٣"), new("Zinc", "زنك"), new("Iron", "حديد")], [new("30 capsules", "٣٠ كبسولة"), new("60 capsules", "٦٠ كبسولة"), new("90 tablets", "٩٠ قرصًا")], ShelfLifeDays: 540),
        new("MED", "MED", "IQT", "lot", "PCS", 0.1m, 80m, 20, 5000, 6, [new("Surgical gloves", "قفازات جراحية"), new("Face masks", "كمامات"), new("Syringes", "محاقن"), new("Bandages", "ضمادات"), new("Gauze", "شاش"), new("Antiseptic wipes", "مناديل مطهرة")], [new("Box of 50", "علبة ٥٠"), new("Box of 100", "علبة ١٠٠"), new("Small", "صغير"), new("Medium", "متوسط"), new("Large", "كبير")], ShelfLifeDays: 1095),
        new("MEDDEV", "MEDDEV", "IQT", "lot_and_serial", "PCS", 15m, 150m, 1, 4, 2, [new("Blood pressure monitor", "جهاز قياس ضغط الدم"), new("Glucometer", "جهاز قياس السكر"), new("Pulse oximeter", "مقياس الأكسجة"), new("Nebulizer", "جهاز رذاذ"), new("Digital thermometer", "ميزان حرارة رقمي")], [new("Standard", "قياسي"), new("Pro", "احترافي")], ShelfLifeDays: 1825),
        new("PACK", "PACK", "IQT", "none", "PCS", 0.05m, 5m, 500, 20000, 5, [new("Carton box", "كرتونة"), new("Stretch film", "فيلم تغليف"), new("Bubble wrap", "غلاف فقاعي"), new("Tape roll", "شريط لاصق"), new("Label roll", "لفة ملصقات"), new("Pallet wrap", "غلاف منصات")], [new("Small", "صغير"), new("Medium", "متوسط"), new("Large", "كبير"), new("50m", "٥٠ م"), new("100m", "١٠٠ م")], Supplier: "supp:al-furat"),
        new("SPARE", "SPARE", "USI", "none", "PCS", 1m, 300m, 2, 200, 6, [new("Compressor", "ضاغط"), new("Filter", "فلتر"), new("Belt", "سير"), new("Bearing", "محمل"), new("Pump", "مضخة"), new("Valve", "صمام"), new("Motor", "محرك"), new("Gasket", "حشية")], [new("Type A", "نوع أ"), new("Type B", "نوع ب"), new("Type C", "نوع ج"), new("Type D", "نوع د")]),
        new("BUILD", "BUILD", "IQT", "none", "KG", 0.05m, 1.2m, 500, 20000, 4, [new("Cement", "إسمنت"), new("Sand", "رمل"), new("Gravel", "حصى"), new("Gypsum", "جبس"), new("Steel rebar", "حديد تسليح"), new("Lime", "جير"), new("Tile adhesive", "لاصق بلاط")], [new("Grade 32.5", "درجة ٣٢٫٥"), new("Grade 42.5", "درجة ٤٢٫٥"), new("Fine", "ناعم"), new("Coarse", "خشن"), new("12mm", "١٢ ملم")]),
        new("STAT", "STAT", "AEG", "none", "PCS", 0.1m, 15m, 50, 3000, 6, [new("Ballpoint pen", "قلم حبر جاف"), new("Notebook A4", "دفتر A4"), new("Copy paper", "ورق تصوير"), new("Stapler", "دباسة"), new("Folder", "ملف"), new("Marker", "قلم تخطيط"), new("Envelopes", "أظرف")], [new("Pack of 10", "عبوة ١٠"), new("Pack of 12", "عبوة ١٢"), new("Pack of 50", "عبوة ٥٠"), new("500 sheets", "٥٠٠ ورقة"), new("Blue", "أزرق"), new("Black", "أسود")]),
    ];

    private static readonly Warehouse[] Warehouses =
    [
        new("BGD-MAIN", "IQT", "BGD", new("Baghdad main warehouse", "مستودع بغداد الرئيسي"), Bins: true),
        new("BGD-COLD", "IQT", "BGD", new("Baghdad cold store", "مخزن بغداد المبرد"), Bins: true, Cold: true),
        new("BSR-WH", "IQT", "BSR", new("Basra warehouse", "مستودع البصرة")),
        new("EBL-WH", "IQT", "EBL", new("Erbil warehouse", "مستودع أربيل")),
        new("IQT-TRANSIT", "IQT", "BGD", new("Rafidain in transit", "الرافدين – في الطريق"), Kind: "in_transit"),
        new("BGD-BOND", "USI", "BGD", new("Bonded warehouse", "المستودع الجمركي")),
        new("BGD-DIST", "USI", "BGD", new("Distribution centre", "مركز التوزيع")),
        new("USI-TRANSIT", "USI", "BGD", new("Tigris in transit", "دجلة – في الطريق"), Kind: "in_transit"),
        new("JAFZA", "AEG", "DXB", new("Jebel Ali warehouse", "مستودع جبل علي"), Bins: true),
        new("DXB-SHOP", "AEG", "DXB", new("Dubai showroom", "معرض دبي")),
        new("AEG-TRANSIT", "AEG", "DXB", new("Gulf Gate in transit", "بوابة الخليج – في الطريق"), Kind: "in_transit"),
    ];

    private static readonly (string Code, Text Name, string AppliesTo, bool RequiresNote)[] ReasonCodes =
    [
        ("DAMAGE", new("Damaged in storage", "تلف في التخزين"), "adjustment", true), ("SAMPLE", new("Samples issued", "عينات مصروفة"), "adjustment", false), ("EXPIRED", new("Expired stock", "بضاعة منتهية الصلاحية"), "write_off", false),
        ("QC-FAIL", new("Failed quality check", "فشل فحص الجودة"), "scrap", true), ("MISCOUNT", new("Counting difference", "فرق عدّ"), "count", false), ("TRANSIT-LOSS", new("Lost in transit", "فقدان في الطريق"), "shortage", true), ("DEFECT", new("Customer return: defective", "مرتجع زبون: معيب"), "return", true),
    ];

    private static readonly string[] Zones = ["A", "B", "C", "D"];
    private static readonly decimal[] CartonSizes = [6m, 12m, 24m];
    private static readonly string[][] SizeSets = [["S", "M", "L"], ["S", "M", "L", "XL"], ["M", "L"], ["XS", "S", "M", "L", "XL", "XXL"]];
    private static readonly string[] CycleClasses = ["A", "B", "C"];

    private const int ItemCount = 5000;
    private const int LinesPerPosting = 250;

    public static async Task<DemoStockOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(companies);
        var categories = services.GetRequiredService<CategoryService>();
        var masterData = services.GetRequiredService<MasterDataService>();
        var items = services.GetRequiredService<ItemService>();
        var profiles = services.GetRequiredService<ProfileService>();
        var warehouses = services.GetRequiredService<WarehouseService>();
        var reasons = services.GetRequiredService<ReasonCodeService>();
        var posting = services.GetRequiredService<IInventoryPosting>();
        var harness = services.GetRequiredService<IInvariantHarness>();
        var organization = services.GetRequiredService<OrganizationDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        // The load runs inside one transaction: refreshed statistics keep the costing walks and the harness planned for the rows that are really there.
        async Task RefreshStatisticsAsync() => await unitOfWork.Connection.ExecuteAsync(new CommandDefinition("SELECT app.refresh_statistics()", transaction: unitOfWork.Transaction, cancellationToken: cancellationToken));
        // Thousands of saves in one unit of work: what is saved is released from the change trackers, or every save re-scans everything before it.
        var trackers = new DbContext[] { services.GetRequiredService<ItemsDbContext>(), services.GetRequiredService<InventoryDbContext>(), services.GetRequiredService<AccountingDbContext>() };
        void Release()
        {
            foreach (var tracker in trackers)
            {
                tracker.ChangeTracker.Clear();
            }
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        var companyByCode = companies.ToDictionary(static c => c.Definition.Code, static c => c, StringComparer.Ordinal);
        var branchIds = await organization.Branches.Select(static b => new { b.CompanyId, b.Code, b.Id }).ToListAsync(cancellationToken);

        // Master data the items hang from: categories, brands, attributes, posting groups, reason codes.
        foreach (var (code, parent, name) in Categories)
        {
            Require(await categories.CreateAsync(new SaveItemCategoryRequest(code, Bilingual(name), ParentCode: parent), cancellationToken));
        }

        foreach (var brand in Brands)
        {
            Require(await masterData.SaveBrandAsync(null, new SaveBrandRequest(BrandCode(brand), Bilingual(brand)), cancellationToken));
        }

        foreach (var (code, name, values) in Attributes)
        {
            Require(await masterData.SaveAttributeAsync(null, new SaveAttributeRequest(code, Bilingual(name), Values: values.Select((v, i) => new SaveAttributeValueRequest(v.Code, Bilingual(v.Name), i)).ToList()), cancellationToken));
        }

        var postingGroups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, en, ar) in new[] { ("TRADE", "Trading goods", "بضاعة تجارية"), ("FRESH", "Fresh and pharma", "طازج وصيدلاني"), ("EQUIP", "Equipment and devices", "معدات وأجهزة") })
        {
            postingGroups[code] = Require(await profiles.SaveGroupAsync(null, new SavePostingGroupRequest("item", code, Bilingual(en, ar)), cancellationToken)).Id;
        }

        foreach (var (code, name, appliesTo, requiresNote) in ReasonCodes)
        {
            Require(await reasons.CreateAsync(new SaveReasonCodeRequest(code, Bilingual(name), appliesTo, RequiresNote: requiresNote), cancellationToken));
        }

        // Warehouses: eight stocked (three with bins) and one in transit per company.
        var stocked = new Dictionary<string, StockedWarehouse>(StringComparer.Ordinal);
        foreach (var definition in Warehouses)
        {
            var company = companyByCode[definition.Company].Company;
            var branchId = branchIds.SingleOrDefault(b => b.CompanyId == company.Id && b.Code == definition.Branch)?.Id;
            var created = Require(await warehouses.SaveAsync(null, new SaveWarehouseRequest(company.Id, definition.Code, Bilingual(definition.Name), definition.Kind, branchId, BinsEnabled: definition.Bins), cancellationToken));
            var bins = new List<Guid>();
            if (definition.Bins)
            {
                foreach (var zone in Zones)
                {
                    for (var slot = 1; slot <= 12; slot++)
                    {
                        bins.Add(Require(await warehouses.SaveBinAsync(created.Id, null, new SaveBinRequest($"{zone}-{slot:00}", zone, PickSequence: (zone[0] - 'A') * 12 + slot), cancellationToken)).Id);
                    }
                }
            }

            stocked[definition.Code] = new StockedWarehouse(definition, created.Id, company.Id, bins);
        }

        // The items: each family gets its share of the 5,000 by weight; every field derives from the item's ordinal.
        var totalWeight = Families.Sum(static f => f.Weight);
        var lines = new List<Line>();
        var variantCount = 0;
        var lotCount = 0;
        var serialCount = 0;
        var ordinal = 0;
        var openingDate = new DateOnly(today.Year, today.Month, 1);
        for (var f = 0; f < Families.Length; f++)
        {
            var family = Families[f];
            var count = f == Families.Length - 1 ? ItemCount - ordinal : ItemCount * family.Weight / totalWeight;
            var company = companyByCode[family.Company];
            var group = family.Tracking is "lot" or "lot_and_serial" ? postingGroups["FRESH"] : family.Tracking == "serial" ? postingGroups["EQUIP"] : postingGroups["TRADE"];
            var familyWarehouses = stocked.Values.Where(w => w.Definition.Company == family.Company && w.Definition.Kind == "standard").ToList();
            var primary = family.Cold ? familyWarehouses.First(static w => w.Definition.Cold) : familyWarehouses.First(static w => !w.Definition.Cold);
            var others = familyWarehouses.Where(w => w != primary).ToList();
            for (var k = 0; k < count; k++, ordinal++)
            {
                var product = family.Products[k % family.Products.Length];
                var pack = family.Packs[k / family.Products.Length % family.Packs.Length];
                var brand = Brands[k / (family.Products.Length * family.Packs.Length) % Brands.Length];
                var code = $"{family.Code}-{k + 1:0000}";
                var name = new Text($"{brand.En} {product.En} {pack.En}", $"{brand.Ar} {product.Ar} {pack.Ar}");
                var usd = family.MinUsd + (family.MaxUsd - family.MinUsd) * DemoIds.Draw("cost", ordinal, 1000) / 1000m;
                var uoms = new List<SaveItemUomRequest>();
                var barcodes = new List<SaveBarcodeRequest> { new(Ean13("629", ordinal), family.Uom) };
                if (family.Uom == "PCS" && DemoIds.Draw("carton", ordinal, 100) < 35)
                {
                    var perCarton = CartonSizes[DemoIds.Draw("carton-size", ordinal, CartonSizes.Length)];
                    uoms.Add(new SaveItemUomRequest("CTN", Numerator: perCarton, IsPurchaseDefault: true));
                    barcodes.Add(new SaveBarcodeRequest(Ean13("628", ordinal), "CTN"));
                }

                var item = Require(await items.CreateAsync(new SaveItemRequest(
                    code,
                    Bilingual(name),
                    BaseUom: family.Uom,
                    CategoryCode: family.Category,
                    BrandCode: BrandCode(brand),
                    Tracking: family.Tracking,
                    ExpiryRequired: family.ShelfLifeDays > 0,
                    ShelfLifeDays: family.ShelfLifeDays > 0 ? family.ShelfLifeDays : null,
                    Fefo: family.ShelfLifeDays > 0,
                    ItemPostingGroupId: group,
                    ListPrice: Rounding.Round(usd * 1.35m, 2),
                    ListPriceCurrency: "USD",
                    CountryOfOrigin: family.Supplier == "supp:turkish-foods" ? "TR" : family.Company == "AEG" ? "AE" : "CN",
                    Uoms: uoms,
                    Barcodes: barcodes), cancellationToken));

                var variants = new List<Guid?> { null };
                if (family.Variants && DemoIds.Draw("variants", ordinal, 100) < 50)
                {
                    variants.Clear();
                    var sizes = SizeSets[DemoIds.Draw("sizes", ordinal, SizeSets.Length)];
                    var colours = Attributes[1].Values.Skip(DemoIds.Draw("colour", ordinal, 4)).Take(1 + DemoIds.Draw("colours", ordinal, 2)).ToList();
                    foreach (var size in sizes)
                    {
                        foreach (var colour in colours)
                        {
                            var variant = Require(await items.SaveVariantAsync(item.Id, null, new SaveVariantRequest(
                                $"{code}-{size}-{colour.Code}",
                                Bilingual(new($"{name.En} {size} {colour.Name.En}", $"{name.Ar} {size} {colour.Name.Ar}")),
                                new Dictionary<string, string>(StringComparer.Ordinal) { ["SIZE"] = size, ["COLOUR"] = colour.Code }), cancellationToken));
                            variants.Add(variant.Id);
                            variantCount++;
                        }
                    }
                }

                if (DemoIds.Draw("supplier", ordinal, 100) < 40)
                {
                    Require(await items.SaveSupplierAsync(item.Id, null, new SaveItemSupplierRequest(DemoIds.For("party:" + family.Supplier), "SUP-" + code, LeadTimeDays: 7 + DemoIds.Draw("lead", ordinal, 22), LastPrice: Rounding.Round(usd, 2), LastPriceCurrency: "USD", IsPreferred: true), cancellationToken));
                }

                Release();

                // Opening stock: most items in their family's primary warehouse, a third also in a second one.
                var unitCost = FunctionalCost(usd, company.Definition.FunctionalCurrency);
                var targets = new List<StockedWarehouse>();
                if (DemoIds.Draw("stocked", ordinal, 100) < 75)
                {
                    targets.Add(primary);
                }

                if (others.Count > 0 && DemoIds.Draw("second", ordinal, 100) < 25)
                {
                    targets.Add(others[DemoIds.Draw("second-pick", ordinal, others.Count)]);
                }

                var serialSequence = 0;
                for (var t = 0; t < targets.Count; t++)
                {
                    var warehouse = targets[t];
                    Guid? bin = warehouse.Bins.Count > 0 ? warehouse.Bins[DemoIds.Draw("bin", ordinal * 4 + t, warehouse.Bins.Count)] : null;
                    foreach (var variantId in variants)
                    {
                        var point = ordinal * 16 + t * 4 + variants.IndexOf(variantId);
                        var quantity = family.MinQty + DemoIds.Draw("qty", point, family.MaxQty - family.MinQty + 1);
                        if (variantId is not null)
                        {
                            quantity = Math.Max(1, quantity / variants.Count);
                        }

                        if (family.Tracking is "lot" or "lot_and_serial")
                        {
                            var lots = 1 + DemoIds.Draw("lots", point, 2);
                            var perLot = Math.Max(1, quantity / lots);
                            for (var l = 0; l < lots; l++)
                            {
                                var lotIndex = t * 2 + l;
                                var manufactured = openingDate.AddDays(-(20 + DemoIds.Draw("mfg", ordinal * 8 + lotIndex, 160)));
                                var expires = manufactured.AddDays(family.ShelfLifeDays);
                                if (expires < openingDate.AddDays(15))
                                {
                                    expires = openingDate.AddDays(Math.Max(15, family.ShelfLifeDays / 2));
                                }

                                var lotNumber = $"L{manufactured:yyMM}-{100 + DemoIds.Draw("lotno", ordinal * 8 + lotIndex, 900)}";
                                var serials = family.Tracking == "lot_and_serial" ? Serials(code, ref serialSequence, perLot) : null;
                                lines.Add(new Line(company.Company.Id, warehouse.Definition.Code, new StockLine(item.Id, StockEntryTypes.Opening, perLot, warehouse.Id, VariantId: variantId, BinId: bin, UnitCost: unitCost, LotNumber: lotNumber, ExpiresOn: expires, ManufacturedOn: manufactured, SerialNumbers: serials)));
                                lotCount++;
                                serialCount += serials?.Count ?? 0;
                            }
                        }
                        else if (family.Tracking == "serial")
                        {
                            var serials = Serials(code, ref serialSequence, quantity);
                            lines.Add(new Line(company.Company.Id, warehouse.Definition.Code, new StockLine(item.Id, StockEntryTypes.Opening, quantity, warehouse.Id, VariantId: variantId, BinId: bin, UnitCost: unitCost, SerialNumbers: serials)));
                            serialCount += serials.Count;
                        }
                        else
                        {
                            lines.Add(new Line(company.Company.Id, warehouse.Definition.Code, new StockLine(item.Id, StockEntryTypes.Opening, quantity, warehouse.Id, VariantId: variantId, BinId: bin, UnitCost: unitCost)));
                        }
                    }

                    // Planning parameters and a cycle-count class for a quarter of the stocked items, so the planner and the counts have work.
                    if (t == 0 && DemoIds.Draw("planning", ordinal, 100) < 25)
                    {
                        // A third of the way into the family's opening range, so about a third of the planned rows start below it.
                        var reorder = Math.Max(1, family.MinQty + ((family.MaxQty - family.MinQty) / 3));
                        Require(await items.SaveWarehouseSettingsAsync(item.Id, warehouse.Id, new SaveWarehouseSettingsRequest(
                            ReorderPoint: reorder,
                            MinQty: Math.Max(1, reorder / 2),
                            MaxQty: reorder * 4,
                            SafetyStock: Math.Max(1, reorder / 5),
                            LeadTimeDays: 5 + DemoIds.Draw("wh-lead", ordinal, 20),
                            DefaultBinId: bin,
                            CycleCountClass: CycleClasses[DemoIds.Draw("class", ordinal, CycleClasses.Length)]), cancellationToken));
                    }
                }
            }
        }

        Console.WriteLine($"Demo seed: {ordinal} items with {variantCount} variants in {Warehouses.Length} warehouses ({phase.Elapsed.TotalSeconds:0} s); posting the opening stock through the costing engine…");
        phase.Restart();
        await RefreshStatisticsAsync();
        // Opening stock through the posting engine, one document per warehouse and batch: quantities, lots, serials, costs and the GL side.
        var stockLines = 0;
        foreach (var batch in lines.GroupBy(static l => (l.CompanyId, l.Warehouse)))
        {
            var chunk = 0;
            foreach (var slice in batch.Chunk(LinesPerPosting))
            {
                var request = new StockPostingRequest(batch.Key.CompanyId, openingDate, "demo_opening", DemoIds.For($"opening:{batch.Key.Warehouse}", chunk), slice.Select(static l => l.Stock).ToList(), $"demo-opening:{batch.Key.Warehouse}:{chunk}");
                var result = Require(await posting.PostAsync(request, cancellationToken));
                stockLines += result.Entries.Count;
                chunk++;
                Release();
            }
        }

        Console.WriteLine($"Demo seed: {stockLines} opening stock entries valued and booked ({phase.Elapsed.TotalSeconds:0} s); running the invariant harness…");
        await RefreshStatisticsAsync();
        var report = await harness.RunAsync(null, cancellationToken);
        if (!report.Passed)
        {
            throw new InvalidOperationException("Demo seed: the invariant harness failed after the stock: " + string.Join(" | ", report.Checks.Where(static c => !c.Passed).Select(static c => c.Code + ": " + string.Join("; ", c.Problems))));
        }

        return new DemoStockOutcome(Warehouses.Length, ordinal, variantCount, lotCount, serialCount, stockLines);
    }

    private static List<string> Serials(string code, ref int sequence, decimal quantity)
    {
        var serials = new List<string>();
        for (var n = 0; n < (int)quantity; n++)
        {
            sequence++;
            serials.Add($"{code}-{sequence:0000}");
        }

        return serials;
    }

    private static decimal FunctionalCost(decimal usd, string currency) => currency switch
    {
        "IQD" => Rounding.Round(usd * DemoRates.OfficialUsdIqd, 0),
        "AED" => Rounding.Round(usd * DemoRates.UsdAedPeg, 2),
        _ => Rounding.Round(usd, 2),
    };

    /// <summary>A GS1-style EAN-13: a prefix, nine digits from the ordinal, and the check digit.</summary>
    internal static string Ean13(string prefix, int ordinal)
    {
        var body = prefix + ordinal.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = body[i] - '0';
            sum += i % 2 == 0 ? digit : digit * 3;
        }

        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BrandCode(Text brand) => brand.En.ToUpperInvariant().Replace(' ', '-').Replace("'", string.Empty, StringComparison.Ordinal);

    private static Dictionary<string, string> Bilingual(Text text) => Bilingual(text.En, text.Ar);

    private static Dictionary<string, string> Bilingual(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed (stock) failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
