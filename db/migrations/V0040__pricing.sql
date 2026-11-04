-- M5 slice 5.2: the pricing engine's rule set (ADR-0030, DOMAIN_MODEL §11). Price lists per company with quantity
-- breaks, validity, tax inclusivity and derivation from a parent list (plus or minus a percentage, rounded by rule,
-- converted when the currencies differ), assigned to customers and customer groups; customer price agreements; line
-- and document discount rules; promotions (buy X get Y, bundles, volume tiers, coupons) with their usage; price
-- floors. The engine reads these as of a pricing date and explains every price it gives (A-144).

CREATE TABLE app.prc_price_lists (
  tenant_id              uuid NOT NULL REFERENCES control.tenants (id),
  id                     uuid NOT NULL,
  company_id             uuid NOT NULL,
  code                   text NOT NULL,
  name_i18n              jsonb NOT NULL DEFAULT '{}'::jsonb,
  currency               text NOT NULL REFERENCES control.currencies (code),
  prices_include_tax     boolean NOT NULL DEFAULT false,
  parent_list_id         uuid,
  parent_adjustment_pct  numeric(9,4) CHECK (parent_adjustment_pct IS NULL OR (parent_adjustment_pct > -100 AND parent_adjustment_pct <= 1000)),
  rounding_increment     numeric(24,10) CHECK (rounding_increment IS NULL OR rounding_increment > 0),
  rounding_mode          text NOT NULL DEFAULT 'nearest' CHECK (rounding_mode IN ('nearest', 'up', 'down')),
  price_surcharge        numeric(24,10) NOT NULL DEFAULT 0,
  valid_from             date,
  valid_to               date,
  priority               int NOT NULL DEFAULT 100 CHECK (priority >= 0),
  is_default             boolean NOT NULL DEFAULT false,
  is_active              boolean NOT NULL DEFAULT true,
  notes                  text,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, parent_list_id) REFERENCES app.prc_price_lists (tenant_id, id),
  CHECK (parent_list_id IS DISTINCT FROM id),
  CHECK ((parent_list_id IS NULL) = (parent_adjustment_pct IS NULL)),
  CHECK (parent_list_id IS NOT NULL OR (rounding_increment IS NULL AND price_surcharge = 0)),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.prc_price_lists');
CALL app.track_updated_at('app.prc_price_lists');
-- One default list per company is the last list the engine looks at before the item's own list price.
CREATE UNIQUE INDEX prc_price_lists_default_idx ON app.prc_price_lists (tenant_id, company_id) WHERE is_default;
CREATE INDEX prc_price_lists_parent_idx ON app.prc_price_lists (tenant_id, parent_list_id) WHERE parent_list_id IS NOT NULL;

-- Who a list is for: specific customers or a customer group. A list with no assignment serves only when a document
-- names it or it is the company default.
CREATE TABLE app.prc_price_list_assignments (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  id                 uuid NOT NULL,
  price_list_id      uuid NOT NULL,
  partner_id         uuid,
  customer_group_id  uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, price_list_id, partner_id, customer_group_id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES app.prc_price_lists (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_group_id) REFERENCES app.ptr_customer_groups (tenant_id, id),
  CHECK (num_nonnulls(partner_id, customer_group_id) = 1)
);
CALL app.enable_tenant_rls('app.prc_price_list_assignments');
CREATE INDEX prc_price_list_assignments_partner_idx ON app.prc_price_list_assignments (tenant_id, partner_id) WHERE partner_id IS NOT NULL;
CREATE INDEX prc_price_list_assignments_group_idx ON app.prc_price_list_assignments (tenant_id, customer_group_id) WHERE customer_group_id IS NOT NULL;

-- A price of an item (or one variant) per unit, from a quantity (the break) and a date. For one item, variant, unit and
-- break the entry with the latest start on or before the pricing date applies, so price changes are scheduled rows.
CREATE TABLE app.prc_price_list_items (
  tenant_id      uuid NOT NULL REFERENCES control.tenants (id),
  id             uuid NOT NULL,
  price_list_id  uuid NOT NULL,
  item_id        uuid NOT NULL,
  variant_id     uuid,
  uom_id         uuid NOT NULL,
  min_quantity   numeric(24,9) NOT NULL DEFAULT 0 CHECK (min_quantity >= 0),
  price          numeric(24,10) NOT NULL CHECK (price >= 0),
  valid_from     date,
  valid_to       date,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, price_list_id, item_id, variant_id, uom_id, min_quantity, valid_from),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES app.prc_price_lists (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.prc_price_list_items');
CALL app.track_updated_at('app.prc_price_list_items');
CREATE INDEX prc_price_list_items_item_idx ON app.prc_price_list_items (tenant_id, item_id, price_list_id);

-- What was agreed with one customer: a net price for an item (per unit, from a quantity) or a discount on an item or a
-- whole category. The most specific agreement wins: a variant over its item, an item over its category, a nearer
-- category over a farther one.
CREATE TABLE app.prc_customer_price_agreements (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  id            uuid NOT NULL,
  company_id    uuid NOT NULL,
  partner_id    uuid NOT NULL,
  reference     text,
  item_id       uuid,
  variant_id    uuid,
  category_id   uuid,
  uom_id        uuid,
  min_quantity  numeric(24,9) NOT NULL DEFAULT 0 CHECK (min_quantity >= 0),
  price         numeric(24,10) CHECK (price IS NULL OR price >= 0),
  currency      text REFERENCES control.currencies (code),
  discount_pct  numeric(9,4) CHECK (discount_pct IS NULL OR (discount_pct > 0 AND discount_pct <= 100)),
  valid_from    date,
  valid_to      date,
  is_active     boolean NOT NULL DEFAULT true,
  notes         text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, partner_id, item_id, variant_id, category_id, uom_id, min_quantity, valid_from),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id),
  CHECK (num_nonnulls(item_id, category_id) = 1),
  CHECK (variant_id IS NULL OR item_id IS NOT NULL),
  CHECK (num_nonnulls(price, discount_pct) = 1),
  CHECK (price IS NULL OR (item_id IS NOT NULL AND uom_id IS NOT NULL AND currency IS NOT NULL)),
  CHECK (discount_pct IS NULL OR (uom_id IS NULL AND currency IS NULL)),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.prc_customer_price_agreements');
CALL app.track_updated_at('app.prc_customer_price_agreements');
CREATE INDEX prc_customer_price_agreements_partner_idx ON app.prc_customer_price_agreements (tenant_id, company_id, partner_id);

-- A discount rule. Every scope column that is set must match (an item, a category and its descendants, a brand, a
-- customer, a customer group, a sales channel, payment terms) and every condition must hold (a minimum quantity in the
-- item's base unit, a minimum amount, the weekday of the pricing date). Exclusive rules compete and the best for the
-- customer wins; stackable rules apply one after the other on the running net, in priority order.
CREATE TABLE app.prc_discount_rules (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  code               text NOT NULL,
  name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  level              text NOT NULL CHECK (level IN ('line', 'document')),
  item_id            uuid,
  category_id        uuid,
  brand_id           uuid,
  partner_id         uuid,
  customer_group_id  uuid,
  channel            text,
  payment_terms_id   uuid,
  min_quantity       numeric(24,9) CHECK (min_quantity IS NULL OR min_quantity > 0),
  min_amount         numeric(24,6) CHECK (min_amount IS NULL OR min_amount > 0),
  weekdays           int[] CHECK (weekdays IS NULL OR (cardinality(weekdays) BETWEEN 1 AND 7 AND weekdays <@ ARRAY[0, 1, 2, 3, 4, 5, 6])),
  value_type         text NOT NULL CHECK (value_type IN ('percentage', 'amount', 'fixed_price')),
  value              numeric(24,10) NOT NULL CHECK (value >= 0),
  currency           text REFERENCES control.currencies (code),
  combination        text NOT NULL DEFAULT 'exclusive' CHECK (combination IN ('exclusive', 'stackable')),
  priority           int NOT NULL DEFAULT 100 CHECK (priority >= 0),
  valid_from         date,
  valid_to           date,
  is_active          boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_id) REFERENCES app.itm_brands (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_group_id) REFERENCES app.ptr_customer_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  CHECK (value_type <> 'percentage' OR value <= 100),
  CHECK ((value_type = 'percentage' AND min_amount IS NULL) = (currency IS NULL)),
  CHECK (level = 'line' OR (value_type <> 'fixed_price' AND item_id IS NULL AND category_id IS NULL AND brand_id IS NULL AND min_quantity IS NULL)),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.prc_discount_rules');
CALL app.track_updated_at('app.prc_discount_rules');

-- A promotion, evaluated on the document's lines as a set after line discounts:
--   buy_x_get_y  every buy_quantity bought in scope earns get_quantity of get_item (the same item when the scope is one
--                item and no other is named) at get_discount_pct off, as its own line;
--   bundle       every complete set of the components sells for bundle_price;
--   volume_tier  the quantity bought in scope reaches a tier and every line in scope gets its discount;
--   coupon       the lines in scope (all lines when none is set) get discount_pct when the coupon code is presented.
-- Any promotion may require a coupon code. Exclusive promotions do not share a line with another promotion.
CREATE TABLE app.prc_promotions (
  tenant_id                 uuid NOT NULL REFERENCES control.tenants (id),
  id                        uuid NOT NULL,
  company_id                uuid NOT NULL,
  code                      text NOT NULL,
  name_i18n                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind                      text NOT NULL CHECK (kind IN ('buy_x_get_y', 'bundle', 'volume_tier', 'coupon')),
  coupon_code               text,
  item_id                   uuid,
  category_id               uuid,
  brand_id                  uuid,
  partner_id                uuid,
  customer_group_id         uuid,
  channel                   text,
  buy_quantity              numeric(24,9) CHECK (buy_quantity IS NULL OR buy_quantity > 0),
  get_item_id               uuid,
  get_quantity              numeric(24,9) CHECK (get_quantity IS NULL OR get_quantity > 0),
  get_discount_pct          numeric(9,4) CHECK (get_discount_pct IS NULL OR (get_discount_pct > 0 AND get_discount_pct <= 100)),
  max_applications          int CHECK (max_applications IS NULL OR max_applications > 0),
  bundle_price              numeric(24,10) CHECK (bundle_price IS NULL OR bundle_price >= 0),
  currency                  text REFERENCES control.currencies (code),
  discount_pct              numeric(9,4) CHECK (discount_pct IS NULL OR (discount_pct > 0 AND discount_pct <= 100)),
  combination               text NOT NULL DEFAULT 'exclusive' CHECK (combination IN ('exclusive', 'stackable')),
  priority                  int NOT NULL DEFAULT 100 CHECK (priority >= 0),
  usage_limit               int CHECK (usage_limit IS NULL OR usage_limit > 0),
  usage_limit_per_customer  int CHECK (usage_limit_per_customer IS NULL OR usage_limit_per_customer > 0),
  valid_from                date,
  valid_to                  date,
  is_active                 boolean NOT NULL DEFAULT true,
  created_at                timestamptz NOT NULL DEFAULT now(),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, brand_id) REFERENCES app.itm_brands (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_group_id) REFERENCES app.ptr_customer_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, get_item_id) REFERENCES app.itm_items (tenant_id, id),
  CHECK (kind <> 'buy_x_get_y' OR (buy_quantity IS NOT NULL AND get_quantity IS NOT NULL AND get_discount_pct IS NOT NULL
         AND num_nonnulls(item_id, category_id, brand_id) >= 1 AND (get_item_id IS NOT NULL OR item_id IS NOT NULL))),
  CHECK (kind <> 'bundle' OR (bundle_price IS NOT NULL AND currency IS NOT NULL)),
  CHECK (kind <> 'volume_tier' OR num_nonnulls(item_id, category_id, brand_id) >= 1),
  CHECK (kind <> 'coupon' OR (coupon_code IS NOT NULL AND discount_pct IS NOT NULL)),
  CHECK ((kind = 'bundle') = (bundle_price IS NOT NULL)),
  CHECK ((kind = 'bundle') = (currency IS NOT NULL)),
  CHECK ((kind = 'coupon') = (discount_pct IS NOT NULL)),
  CHECK ((kind = 'buy_x_get_y') = (buy_quantity IS NOT NULL)),
  CHECK (kind = 'buy_x_get_y' OR (get_item_id IS NULL AND get_quantity IS NULL AND get_discount_pct IS NULL AND max_applications IS NULL)),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.prc_promotions');
CALL app.track_updated_at('app.prc_promotions');
CREATE UNIQUE INDEX prc_promotions_coupon_idx ON app.prc_promotions (tenant_id, company_id, upper(coupon_code)) WHERE coupon_code IS NOT NULL;

-- The items of a bundle and how many of each (base unit) make one set.
CREATE TABLE app.prc_promotion_components (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  promotion_id  uuid NOT NULL,
  item_id       uuid NOT NULL,
  quantity      numeric(24,9) NOT NULL CHECK (quantity > 0),
  PRIMARY KEY (tenant_id, promotion_id, item_id),
  FOREIGN KEY (tenant_id, promotion_id) REFERENCES app.prc_promotions (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.prc_promotion_components');

-- The tiers of a volume promotion: from a quantity in scope (base unit), a discount on every line in scope.
CREATE TABLE app.prc_promotion_tiers (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  promotion_id  uuid NOT NULL,
  min_quantity  numeric(24,9) NOT NULL CHECK (min_quantity > 0),
  discount_pct  numeric(9,4) NOT NULL CHECK (discount_pct > 0 AND discount_pct <= 100),
  PRIMARY KEY (tenant_id, promotion_id, min_quantity),
  FOREIGN KEY (tenant_id, promotion_id) REFERENCES app.prc_promotions (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.prc_promotion_tiers');

-- A promotion used by a confirmed document; counted against the usage limits until the document gives it back.
CREATE TABLE app.prc_promotion_usages (
  tenant_id      uuid NOT NULL REFERENCES control.tenants (id),
  id             uuid NOT NULL,
  promotion_id   uuid NOT NULL,
  partner_id     uuid,
  document_type  text NOT NULL,
  document_id    uuid NOT NULL,
  used_at        timestamptz NOT NULL,
  released_at    timestamptz,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, promotion_id, document_type, document_id),
  FOREIGN KEY (tenant_id, promotion_id) REFERENCES app.prc_promotions (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  CHECK (released_at IS NULL OR released_at >= used_at)
);
CALL app.enable_tenant_rls('app.prc_promotion_usages');
CREATE INDEX prc_promotion_usages_live_idx ON app.prc_promotion_usages (tenant_id, promotion_id, partner_id) WHERE released_at IS NULL;

-- The lowest price, or the thinnest margin over expected cost, an item (or every item of a category) may sell at.
CREATE TABLE app.prc_price_floors (
  tenant_id       uuid NOT NULL REFERENCES control.tenants (id),
  id              uuid NOT NULL,
  company_id      uuid NOT NULL,
  item_id         uuid,
  category_id     uuid,
  min_price       numeric(24,10) CHECK (min_price IS NULL OR min_price >= 0),
  currency        text REFERENCES control.currencies (code),
  min_margin_pct  numeric(9,4) CHECK (min_margin_pct IS NULL OR (min_margin_pct > -100 AND min_margin_pct < 100)),
  on_breach       text NOT NULL DEFAULT 'block' CHECK (on_breach IN ('block', 'warn')),
  is_active       boolean NOT NULL DEFAULT true,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, item_id, category_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  CHECK (num_nonnulls(item_id, category_id) = 1),
  CHECK (num_nonnulls(min_price, min_margin_pct) >= 1),
  CHECK ((min_price IS NULL) = (currency IS NULL))
);
CALL app.enable_tenant_rls('app.prc_price_floors');
CALL app.track_updated_at('app.prc_price_floors');
