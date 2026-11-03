/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "true" switches the Çeliknaz app to local fixtures and a simulated device hub. */
  readonly VITE_USE_MOCK?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
