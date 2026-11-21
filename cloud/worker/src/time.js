// Turns a date value into an ISO string, or null.
export function iso(value) {
  if (value === null || value === undefined || value === "") return null;
  let s = String(value);
  // BGP feeds send UTC times without a zone ("2026-11-21T01:44:53.019").
  if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(s)) s += "Z";
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}
