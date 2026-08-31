(() => {
  "use strict";

  const JS_URL =
    "https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/SkyStreamRemote/sky_remote.js?v=local-waits&t=" +
    Date.now();

  function showError(message) {
    document.body.textContent = "";
    const pre = document.createElement("pre");
    pre.style.cssText = "padding:16px;color:#ff7186;font:14px/1.4 sans-serif;white-space:pre-wrap";
    pre.textContent = message;
    document.body.appendChild(pre);
  }

  async function boot() {
    try {
      const response = await fetch(JS_URL, { cache: "no-store" });
      if (!response.ok) {
        throw new Error("sky_remote.js: HTTP " + response.status + " from GitHub");
      }

      const code = await response.text();
      const jsUrl = URL.createObjectURL(new Blob([code], { type: "text/javascript" }));

      await new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = jsUrl;
        script.onload = resolve;
        script.onerror = () => reject(new Error("sky_remote.js failed to run"));
        document.head.appendChild(script);
      });
    } catch (error) {
      console.error(error);
      showError("Could not load Sky Stream remote from GitHub.\n" + error.message);
    }
  }

  boot();
})();
