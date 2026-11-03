/** Route paths (Turkish slugs, hash based). */
export const paths = {
  home: "/",
  login: "/giris",
  ev: "/ev",
  evTasks: "/ev/gorevler",
  evAssistant: "/ev/asistan",
  evDevicePattern: "/ev/cihaz/:id",
} as const;

/** Path of the appliance detail screen. */
export function devicePath(id: string): string {
  return `/ev/cihaz/${encodeURIComponent(id)}`;
}
