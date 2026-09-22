-- V0014: items and units of measure (roadmap 3.1, DOMAIN_MODEL §8, ADR-0008 for what inventory reads from here).
-- Items with categories (materialised path), brands, variants defined by attribute values, item units of measure with
-- exact rational factors to the base unit (hard scenario 9), barcodes, suppliers per item, settings per company and
-- per warehouse, bills of material (kits and assemblies), substitutes. Quantities are numeric(24,9) in the base unit
-- (ASSUMPTIONS A-025); unit costs and prices numeric(24,10). Partner, tax-group, warehouse, bin and attachment
-- references are plain uuids until their modules land (ASSUMPTIONS A-096); the warehouse foreign key is added by the
-- inventory migration of 3.2.

CREATE TABLE app.itm_brands (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.itm_brands');
CALL app.track_updated_at('app.itm_brands');

CREATE TABLE app.itm_item_categories (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  parent_id                uuid,
  code                     text NOT NULL,
  name_i18n                jsonb NOT NULL DEFAULT '{}'::jsonb,
  path                     text NOT NULL,
  level                    int NOT NULL DEFAULT 0 CHECK (level >= 0),
  costing_method_override  text CHECK (costing_method_override IN ('fifo', 'average', 'standard')),
  item_posting_group_id    uuid,
  item_tax_group_id        uuid,
  is_active                boolean NOT NULL DEFAULT true,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, parent_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, item_posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  CHECK (parent_id IS NULL OR parent_id <> id)
);
CREATE INDEX itm_item_categories_path_idx ON app.itm_item_categories (tenant_id, path text_pattern_ops);
CALL app.enable_tenant_rls('app.itm_item_categories');
CALL app.track_updated_at('app.itm_item_categories');

CREATE TABLE app.itm_attributes (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  sort_order  int NOT NULL DEFAULT 0,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.itm_attributes');
CALL app.track_updated_at('app.itm_attributes');

CREATE TABLE app.itm_attribute_values (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  attribute_id  uuid NOT NULL,
  code          text NOT NULL,
  name_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  sort_order    int NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, attribute_id, code),
  FOREIGN KEY (tenant_id, attribute_id) REFERENCES app.itm_attributes (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.itm_attribute_values');

CREATE TABLE app.itm_items (
  tenant_id              uuid NOT NULL REFERENCES control.tenants (id),
  id                     uuid NOT NULL,
  code                   text NOT NULL,
  name_i18n              jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n       jsonb NOT NULL DEFAULT '{}'::jsonb,
  category_id            uuid,
  brand_id               uuid,
  type                   text NOT NULL CHECK (type IN ('stock', 'non_stock', 'service', 'kit', 'assembly')),
  base_uom_id            uuid NOT NULL,
  sales_uom_id           uuid,
  purchase_uom_id        uuid,
  tracking               text NOT NULL DEFAULT 'none' CHECK (tracking IN ('none', 'lot', 'serial', 'lot_and_serial')),
  expiry_required        boolean NOT NULL DEFAULT false,
  shelf_life_days        int CHECK (shelf_life_days IS NULL OR shelf_life_days > 0),
  fefo                   boolean NOT NULL DEFAULT false,
  item_posting_group_id  uuid,
  item_tax_group_id      uuid,
  list_price             numeric(24, 10) CHECK (list_price IS NULL OR list_price >= 0),
  list_price_currency    text,
  weight_kg              numeric(20, 6) CHECK (weight_kg IS NULL OR weight_kg >= 0),
  volume_m3              numeric(20, 6) CHECK (volume_m3 IS NULL OR volume_m3 >= 0),
  hs_code                text,
  country_of_origin      text,
  has_variants           boolean NOT NULL DEFAULT false,
  image_attachment_id    uuid,
  is_active              boolean NOT NULL DEFAULT true,
  custom_fields          jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_id) REFERENCES app.itm_brands (tenant_id, id),
  FOREIGN KEY (tenant_id, base_uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, sales_uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, purchase_uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, item_posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  CHECK (NOT expiry_required OR tracking IN ('lot', 'lot_and_serial')),
  CHECK (NOT fefo OR tracking IN ('lot', 'lot_and_serial'))
);
CREATE INDEX itm_items_category_idx ON app.itm_items (tenant_id, category_id);
CALL app.enable_tenant_rls('app.itm_items');
CALL app.track_updated_at('app.itm_items');

CREATE TABLE app.itm_item_variants (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  item_id              uuid NOT NULL,
  sku                  text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  attribute_values     jsonb NOT NULL DEFAULT '{}'::jsonb,
  image_attachment_id  uuid,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, sku),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX itm_item_variants_item_idx ON app.itm_item_variants (tenant_id, item_id);
CALL app.enable_tenant_rls('app.itm_item_variants');
CALL app.track_updated_at('app.itm_item_variants');

-- 1 unit of uom_id = numerator / denominator base units; the base unit's own row is 1/1 and is created with the item.
CREATE TABLE app.itm_item_uoms (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  item_id              uuid NOT NULL,
  uom_id               uuid NOT NULL,
  numerator            numeric(24, 12) NOT NULL CHECK (numerator > 0),
  denominator          numeric(24, 12) NOT NULL CHECK (denominator > 0),
  weight_kg            numeric(20, 6) CHECK (weight_kg IS NULL OR weight_kg >= 0),
  dimensions           jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_purchase_default  boolean NOT NULL DEFAULT false,
  is_sales_default     boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, item_id, uom_id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id)
);
CALL app.enable_tenant_rls('app.itm_item_uoms');

CREATE TABLE app.itm_item_barcodes (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  item_uom_id  uuid NOT NULL,
  variant_id   uuid,
  barcode      text NOT NULL,
  symbology    text NOT NULL DEFAULT 'EAN13' CHECK (symbology IN ('EAN13', 'EAN8', 'UPCA', 'CODE128', 'CODE39', 'QR', 'DATAMATRIX', 'OTHER')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, barcode),
  FOREIGN KEY (tenant_id, item_uom_id) REFERENCES app.itm_item_uoms (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.itm_item_barcodes');

CREATE TABLE app.itm_item_suppliers (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  item_id              uuid NOT NULL,
  partner_id           uuid NOT NULL,
  supplier_item_code   text,
  uom_id               uuid,
  lead_time_days       int CHECK (lead_time_days IS NULL OR lead_time_days >= 0),
  last_price           numeric(24, 10) CHECK (last_price IS NULL OR last_price >= 0),
  last_price_currency  text,
  is_preferred         boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, item_id, partner_id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id)
);
CALL app.enable_tenant_rls('app.itm_item_suppliers');

CREATE TABLE app.itm_item_company_settings (
  tenant_id                    uuid NOT NULL,
  item_id                      uuid NOT NULL,
  company_id                   uuid NOT NULL,
  costing_method_override      text CHECK (costing_method_override IN ('fifo', 'average', 'standard')),
  standard_cost                numeric(24, 10) CHECK (standard_cost IS NULL OR standard_cost >= 0),
  item_posting_group_override  uuid,
  default_warehouse_id         uuid,
  allow_negative_stock         boolean,
  updated_at                   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, item_id, company_id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_posting_group_override) REFERENCES app.gl_posting_groups (tenant_id, id)
);
CALL app.enable_tenant_rls('app.itm_item_company_settings');
CALL app.track_updated_at('app.itm_item_company_settings');

CREATE TABLE app.itm_item_warehouse_settings (
  tenant_id           uuid NOT NULL,
  item_id             uuid NOT NULL,
  warehouse_id        uuid NOT NULL,
  reorder_point       numeric(24, 9) CHECK (reorder_point IS NULL OR reorder_point >= 0),
  min_qty             numeric(24, 9) CHECK (min_qty IS NULL OR min_qty >= 0),
  max_qty             numeric(24, 9) CHECK (max_qty IS NULL OR max_qty >= 0),
  safety_stock        numeric(24, 9) CHECK (safety_stock IS NULL OR safety_stock >= 0),
  lead_time_days      int CHECK (lead_time_days IS NULL OR lead_time_days >= 0),
  default_bin_id      uuid,
  cycle_count_class   text CHECK (cycle_count_class IN ('A', 'B', 'C')),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, item_id, warehouse_id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  CHECK (min_qty IS NULL OR max_qty IS NULL OR min_qty <= max_qty)
);
CALL app.enable_tenant_rls('app.itm_item_warehouse_settings');
CALL app.track_updated_at('app.itm_item_warehouse_settings');

CREATE TABLE app.itm_boms (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  item_id     uuid NOT NULL,
  kind        text NOT NULL CHECK (kind IN ('kit', 'assembly')),
  version     int NOT NULL CHECK (version >= 1),
  output_qty  numeric(24, 9) NOT NULL DEFAULT 1 CHECK (output_qty > 0),
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, item_id, version),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX itm_boms_one_active_idx ON app.itm_boms (tenant_id, item_id) WHERE is_active;
CALL app.enable_tenant_rls('app.itm_boms');
CALL app.track_updated_at('app.itm_boms');

CREATE TABLE app.itm_bom_lines (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  bom_id                uuid NOT NULL,
  position              int NOT NULL DEFAULT 0,
  component_item_id     uuid NOT NULL,
  component_variant_id  uuid,
  quantity              numeric(24, 9) NOT NULL CHECK (quantity > 0),
  uom_id                uuid NOT NULL,
  scrap_pct             numeric(9, 4) NOT NULL DEFAULT 0 CHECK (scrap_pct >= 0 AND scrap_pct < 100),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, bom_id) REFERENCES app.itm_boms (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, component_item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, component_variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id)
);
CALL app.enable_tenant_rls('app.itm_bom_lines');

CREATE TABLE app.itm_substitutes (
  tenant_id           uuid NOT NULL,
  item_id             uuid NOT NULL,
  substitute_item_id  uuid NOT NULL,
  priority            int NOT NULL DEFAULT 1 CHECK (priority >= 1),
  PRIMARY KEY (tenant_id, item_id, substitute_item_id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, substitute_item_id) REFERENCES app.itm_items (tenant_id, id) ON DELETE CASCADE,
  CHECK (item_id <> substitute_item_id)
);
CALL app.enable_tenant_rls('app.itm_substitutes');
