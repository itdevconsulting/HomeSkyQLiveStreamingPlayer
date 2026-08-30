(() => {
  "use strict";

  const REPO = "itdevconsulting/HomeSkyQLiveStreamingPlayer";
  const BASE = `https://raw.githubusercontent.com/${REPO}/main/SkyStreamRemote`;

  function showError(message) {
    document.body.textContent = "";
    const pre = document.createElement("pre");
    pre.style.cssText = "padding:16px;color:#ff7186;font:14px/1.4 sans-serif;white-space:pre-wrap";
    pre.textContent = message;
    document.body.appendChild(pre);
  }

  async function loadAsset(name, mime) {
    const url = `${BASE}/${name}?t=${Date.now()}`;
    const response = await fetch(url, { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`${name}: HTTP ${response.status} from GitHub`);
    }

    const text = await response.text();
    return URL.createObjectURL(new Blob([text], { type: mime }));
  }

  async function boot() {
    try {
      const cssUrl = await loadAsset("sky_remote.css", "text/css");
      const jsUrl = await loadAsset("sky_remote.js", "text/javascript");

      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = cssUrl;
      document.head.appendChild(link);

      await new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = jsUrl;
        script.onload = resolve;
        script.onerror = () => reject(new Error("sky_remote.js failed to run"));
        document.head.appendChild(script);
      });
    } catch (error) {
      console.error(error);
      showError(`Could not load Sky Stream remote from GitHub.\n${error.message}`);
    }
  }

  boot();
})();
