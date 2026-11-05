-- Where each thinking phase of an answer starts (JSON [{start, at, tools}]): the chat shows one thinking block per phase,
-- in its place between the text and the tool calls, instead of all reasoning in one block at the top.
ALTER TABLE ChatMessages ADD COLUMN ReasoningPartsJson TEXT;
