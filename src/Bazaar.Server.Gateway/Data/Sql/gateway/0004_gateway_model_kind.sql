-- Embedding models in the gateway (doc 05 "Embeddings", PROPOSED). Migration namespace: gateway/ (common/ is frozen).
-- kind separates chat models from embedding models: chat routes exclude embedding models and embedding routes exclude chat
-- models. embed_max_batch and embed_max_input_chars are the model's embedding caps; NULL means the provider default.
-- Existing rows are chat models. The new columns add no grants of their own; gw_model keeps whatever privileges common/ gave it.
ALTER TABLE gw_model ADD COLUMN kind text NOT NULL DEFAULT 'chat' CHECK (kind IN ('chat', 'embedding'));
ALTER TABLE gw_model ADD COLUMN embed_max_batch integer NULL CHECK (embed_max_batch IS NULL OR embed_max_batch > 0);
ALTER TABLE gw_model ADD COLUMN embed_max_input_chars integer NULL CHECK (embed_max_input_chars IS NULL OR embed_max_input_chars > 0);
