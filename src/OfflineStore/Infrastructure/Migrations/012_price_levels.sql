-- v12: three per-unit sales price levels, with old retail prices preserved.
-- A missing tier falls back to the current retail price until configured.
CREATE TABLE unit_price_levels (
    product_unit_id INTEGER NOT NULL REFERENCES product_units(id) ON DELETE RESTRICT,
    price_level TEXT NOT NULL CHECK(price_level IN ('SEMI','WHOLESALE')),
    price_cents INTEGER NOT NULL CHECK(typeof(price_cents)='integer' AND price_cents>=0),
    updated_by_user_id INTEGER REFERENCES users(id) ON DELETE RESTRICT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY(product_unit_id,price_level)
);
CREATE INDEX ix_unit_price_levels_editor ON unit_price_levels(updated_by_user_id);
-- The actual paid price already lives in document_lines, with a preserved
-- unit conversion; the tier is supplementary historical metadata only.
ALTER TABLE document_lines ADD COLUMN sale_price_level TEXT
    CHECK(sale_price_level IS NULL OR sale_price_level IN ('RETAIL','SEMI','WHOLESALE'));
