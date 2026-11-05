-- Accounts: a per-account surcharge, sign-ins that survive a restart, and deposits (BTCPay).

-- "Tack on 15% surcharge": every fee this account pays (model tokens, vision, images, videos, its agents) is 15% higher.
ALTER TABLE AdminUsers ADD COLUMN Surcharge INTEGER NOT NULL DEFAULT 0;

-- Web sign-ins, so a restart of the host does not sign everyone out. Only a SHA-256 of the session id is stored: a copy
-- of the database does not hold cookies that could be replayed. Sessions slide; expired ones are removed.
CREATE TABLE IF NOT EXISTS WebSessions (
    IdHash          TEXT PRIMARY KEY,
    Username        TEXT NOT NULL,
    CsrfToken       TEXT NOT NULL,
    ExpiresUtc      TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_WebSessions_User ON WebSessions(Username);

-- Deposits: a BTCPay invoice made for an account (Balance → Deposit) and, once paid, the credit to its balance. The
-- payment server confirms payment through POST /api/payments/deposit; the invoice id makes a credit happen once only.
CREATE TABLE IF NOT EXISTS Deposits (
    InvoiceId       TEXT PRIMARY KEY,
    Username        TEXT NOT NULL,
    AmountMicros    INTEGER NOT NULL,
    Currency        TEXT NOT NULL DEFAULT 'USD',
    Status          TEXT NOT NULL,              -- pending | credited
    CheckoutLink    TEXT,
    CreatedUtc      TEXT NOT NULL,
    CreditedUtc     TEXT,
    CreditedBy      TEXT
);
CREATE INDEX IF NOT EXISTS IX_Deposits_User ON Deposits(Username, CreatedUtc);
