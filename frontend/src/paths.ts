/** Route paths (Turkish slugs, hash based). */
export const paths = {
  home: "/",
  login: "/giris",
  ev: "/ev",
  evTasks: "/ev/gorevler",
  evAssistant: "/ev/asistan",
  evDevicePattern: "/ev/cihaz/:id",
  evRules: "/ev/gorevler/kurallar",
  evSettings: "/ev/ayarlar",
  evNotifications: "/ev/bildirimler",
  evLearning: "/ev/ogrendi",
  panel: "/panel",
} as const;

/** Path of the appliance detail screen. */
export function devicePath(id: string): string {
  return `/ev/cihaz/${encodeURIComponent(id)}`;
}
