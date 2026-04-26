export function redirectToCanonicalHost() {
  const canonicalHost = import.meta.env.VITE_CANONICAL_HOST ?? "";

  if (!canonicalHost || window.location.origin === canonicalHost) {
    return false;
  }

  window.location.replace(`${canonicalHost}${window.location.pathname}${window.location.search}`);
  return true;
}
