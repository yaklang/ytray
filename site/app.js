(() => {
  const releaseURL = "https://aliyun-oss.yaklang.com/ytray/latest.json";
  const releaseFallback = "https://github.com/yaklang/ytray/releases/latest";

  fetch(releaseURL, { headers: { Accept: "application/json" } })
    .then((response) => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    })
    .then((release) => {
      if (!release || !Array.isArray(release.assets)) throw new Error("invalid release manifest");
      const assetMap = new Map(release.assets.map((asset) => [`${asset.platform}:${asset.architecture}`, asset]));
      document.querySelectorAll("[data-asset]").forEach((link) => {
        const asset = assetMap.get(link.dataset.asset);
        if (asset?.url?.startsWith("https://aliyun-oss.yaklang.com/ytray/")) link.href = asset.url;
      });
      const note = document.querySelector("[data-release-note]");
      if (note && typeof release.version === "string") note.textContent = `v${release.version} · macOS 14+ · Windows 10/11 · 源码公开`;
    })
    .catch(() => {
      document.querySelectorAll("[data-download], [data-asset]").forEach((link) => { link.href = releaseFallback; });
    });
})();
