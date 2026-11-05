-- The Money and News tabs. Prices as they were seen (crypto from CoinMarketCap about every minute, exchange rates from
-- Frankfurter / the ECB once a day), for the charts and for agents and chats that ask; the assets' names. News articles from
-- the RSS feeds, one row per link, kept for News.KeepDays.
CREATE TABLE IF NOT EXISTS MarketQuotes (
    Kind TEXT NOT NULL,
    Symbol TEXT NOT NULL,
    Price REAL NOT NULL,
    Utc TEXT NOT NULL,
    PRIMARY KEY (Kind, Symbol, Utc)
);

CREATE TABLE IF NOT EXISTS MarketAssets (
    Kind TEXT NOT NULL,
    Symbol TEXT NOT NULL,
    Name TEXT NULL,
    Slug TEXT NULL,
    PRIMARY KEY (Kind, Symbol)
);

CREATE TABLE IF NOT EXISTS NewsArticles (
    Id TEXT PRIMARY KEY,
    Link TEXT NOT NULL UNIQUE,
    Source TEXT NOT NULL,
    Category TEXT NOT NULL,
    Title TEXT NOT NULL,
    Summary TEXT NULL,
    Author TEXT NULL,
    ImageUrl TEXT NULL,
    PublishedUtc TEXT NOT NULL,
    FetchedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_NewsArticles_Published ON NewsArticles(PublishedUtc);
CREATE INDEX IF NOT EXISTS IX_NewsArticles_Source ON NewsArticles(Source, PublishedUtc);
CREATE INDEX IF NOT EXISTS IX_NewsArticles_Category ON NewsArticles(Category, PublishedUtc);
