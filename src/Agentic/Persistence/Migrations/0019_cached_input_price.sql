-- Model Square: the price of cached input (prompt tokens the provider served from its cache, usually 10% of the input
-- price). NULL = 10% of the input price.
ALTER TABLE ModelSquarePrices ADD COLUMN CachedInputPerMillion TEXT NULL;
