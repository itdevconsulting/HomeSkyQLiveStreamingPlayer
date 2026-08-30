(() => {
  "use strict";

  const COMMAND_DELAY_MS = 500;
  const TV_GUIDE_STEPS = [
    "sky_stream_home",
    "sky_stream_down",
    "sky_stream_down",
    "sky_stream_ok",
    "sky_stream_back",
    "sky_stream_down"
  ];

  let sequenceBusy = false;

  function injectStyles() {
    if (document.getElementById("sky-remote-css")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "sky-remote-css";
    style.textContent = ':root {\n  color-scheme: dark;\n  --page: #071019;\n  --remote: #161b20;\n  --remote-edge: #050709;\n  --button: #20262c;\n  --button-low: #12171b;\n  --text: #f5f7f9;\n  --muted: #8d98a3;\n  --green: #39e56d;\n  --red: #ff2c50;\n  --blue: #178cff;\n  --yellow: #ffd12f;\n}\n\n* {\n  box-sizing: border-box;\n  -webkit-tap-highlight-color: transparent;\n}\n\nhtml, body {\n  margin: 0;\n  min-height: 100%;\n  background:\n    radial-gradient(circle at 50% -15%, #203143 0%, #0c1823 37%, var(--page) 76%);\n  color: var(--text);\n  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;\n}\n\nbody {\n  min-height: 100vh;\n  display: flex;\n  justify-content: center;\n  padding: 16px 8px 28px;\n}\n\n#sky-app {\n  width: min(100%, 320px);\n}\n\n.remote-shell {\n  position: relative;\n  width: 100%;\n  padding: 24px 20px 25px;\n  border: 2px solid var(--remote-edge);\n  border-radius: 42px 42px 72px 72px / 36px 36px 58px 58px;\n  background:\n    linear-gradient(155deg, rgba(255,255,255,.045), transparent 24%),\n    linear-gradient(180deg, #1b2025 0%, #14191e 53%, #0f1418 100%);\n  box-shadow:\n    0 28px 65px rgba(0,0,0,.58),\n    inset 0 1px 1px rgba(255,255,255,.09),\n    inset 0 -2px 5px rgba(0,0,0,.48);\n  user-select: none;\n  overflow: hidden;\n}\n\n.remote-shell::after {\n  content: "";\n  position: absolute;\n  inset: 3px;\n  pointer-events: none;\n  border-radius: inherit;\n  border: 1px solid rgba(255,255,255,.035);\n}\n\n.device-head {\n  display: flex;\n  align-items: center;\n  justify-content: center;\n  gap: 9px;\n  margin: 0 0 20px;\n  min-height: 24px;\n}\n\n.device-led {\n  width: 8px;\n  height: 8px;\n  border-radius: 50%;\n  background: var(--green);\n  box-shadow: 0 0 8px rgba(57,229,109,.65);\n}\n\n.device-host {\n  font-size: 15px;\n  font-weight: 750;\n  letter-spacing: .05px;\n}\n\n.remote-row {\n  display: grid;\n  grid-template-columns: repeat(3, 1fr);\n  align-items: center;\n  gap: 15px;\n  margin-bottom: 14px;\n}\n\n.remote-row.lower {\n  margin: 3px 0 17px;\n}\n\n.remote-button {\n  appearance: none;\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  border: 2px solid #050708;\n  color: var(--text);\n  background: linear-gradient(150deg, var(--button), var(--button-low));\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.11),\n    0 4px 8px rgba(0,0,0,.38);\n  cursor: pointer;\n  touch-action: manipulation;\n  transition: transform 60ms ease, filter 60ms ease;\n}\n\n.remote-button:active,\n.remote-button.active {\n  transform: translateY(2px) scale(.955);\n  filter: brightness(1.28);\n}\n\n.remote-button.round {\n  width: 61px;\n  height: 61px;\n  border-radius: 50%;\n  justify-self: center;\n}\n\n.remote-button.icon {\n  font-size: 22px;\n  line-height: 1;\n}\n\n.dots {\n  display: flex;\n  gap: 3px;\n}\n\n.dot {\n  width: 7px;\n  height: 7px;\n  border-radius: 50%;\n}\n\n.dot.red { background: var(--red); }\n.dot.yellow { background: var(--yellow); }\n.dot.green { background: var(--green); }\n.dot.blue { background: var(--blue); }\n\n.playpause-css {\n  display: flex;\n  align-items: center;\n  justify-content: center;\n  gap: 3px;\n}\n\n.playpause-css .tri {\n  width: 0;\n  height: 0;\n  border-style: solid;\n  border-width: 7px 0 7px 11px;\n  border-color: transparent transparent transparent currentColor;\n}\n\n.playpause-css .bars {\n  display: flex;\n  gap: 3px;\n}\n\n.playpause-css .bars i {\n  display: block;\n  width: 3px;\n  height: 14px;\n  background: currentColor;\n  border-radius: 1px;\n}\n\n.dpad {\n  position: relative;\n  width: 225px;\n  height: 225px;\n  margin: 17px auto 18px;\n  border: 2px solid #050708;\n  border-radius: 50%;\n  background: linear-gradient(145deg, #1d2328, #101519);\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.075),\n    0 6px 14px rgba(0,0,0,.33);\n}\n\n.dpad > button {\n  position: absolute;\n  border: 0;\n  background: transparent;\n  color: #fff;\n  cursor: pointer;\n  touch-action: manipulation;\n  padding: 0;\n  font-size: 28px;\n  line-height: 1;\n}\n\n.dpad > button:active,\n.dpad > button.active {\n  filter: brightness(1.45);\n}\n\n.dpad-up    { top: 5px; left: 66px; width: 90px; height: 62px; }\n.dpad-down  { bottom: 5px; left: 66px; width: 90px; height: 62px; }\n.dpad-left  { left: 5px; top: 66px; width: 62px; height: 90px; }\n.dpad-right { right: 5px; top: 66px; width: 62px; height: 90px; }\n\n.dpad-ok {\n  left: 50%;\n  top: 50%;\n  width: 91px !important;\n  height: 91px !important;\n  transform: translate(-50%, -50%);\n  border-radius: 50% !important;\n  border: 2px solid #050708 !important;\n  background: linear-gradient(150deg, #20262c, #11161a) !important;\n  color: white !important;\n  font-size: 19px;\n  font-weight: 800;\n  box-shadow: inset 0 1px 1px rgba(255,255,255,.09);\n}\n\n.dpad-ok:active,\n.dpad-ok.active {\n  transform: translate(-50%, -48%) scale(.96);\n}\n\n.plus {\n  border-color: var(--red);\n}\n\n.home {\n  border-color: var(--green);\n}\n\n.plus {\n  font-size: 29px;\n  font-weight: 500;\n}\n\n.remote-button.vol {\n  justify-self: center;\n  width: 60px;\n  height: 132px;\n  border-radius: 29px;\n  padding: 0;\n  overflow: hidden;\n  display: grid;\n  grid-template-rows: 1fr auto 1fr;\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.1),\n    0 4px 8px rgba(0,0,0,.38);\n}\n\n.remote-button.vol:active,\n.remote-button.vol.active {\n  transform: none;\n  filter: none;\n}\n\n.vol-part {\n  border: 0;\n  background: transparent;\n  color: white;\n  font-size: 28px;\n  font-weight: 400;\n  cursor: pointer;\n  touch-action: manipulation;\n}\n\n.vol-part:active,\n.vol-part.active {\n  background: rgba(255,255,255,.08);\n}\n\n.vol-label {\n  color: #b6c0ca;\n  text-align: center;\n  font-size: 11px;\n  letter-spacing: .7px;\n}\n\n.guide-row {\n  display: flex;\n  justify-content: center;\n  margin: 14px 0 17px;\n}\n\n.guide-button {\n  width: 170px;\n  height: 46px;\n  border-radius: 23px;\n  font-size: 14px;\n  font-weight: 750;\n  letter-spacing: .2px;\n  gap: 8px;\n}\n\n.guide-button .guide-icon {\n  font-size: 18px;\n}\n\n.keypad {\n  display: grid;\n  grid-template-columns: repeat(3, 61px);\n  justify-content: center;\n  gap: 11px 20px;\n}\n\n.keypad .remote-button {\n  width: 61px;\n  height: 49px;\n  border-radius: 25px;\n  font-size: 17px;\n  font-weight: 750;\n}\n\n.keypad .zero {\n  grid-column: 2;\n}\n\n.status {\n  min-height: 17px;\n  margin-top: 17px;\n  text-align: center;\n  color: var(--muted);\n  font-size: 10px;\n}\n\n.status.ok { color: #69e683; }\n.status.error { color: #ff7186; }\n\n.footer {\n  margin-top: 6px;\n  text-align: center;\n  color: #66717b;\n  font-size: 9px;\n}\n\n@media (max-width: 330px) {\n  body { padding-left: 3px; padding-right: 3px; }\n  #sky-app { width: 310px; }\n  .remote-shell { padding-left: 16px; padding-right: 16px; }\n}\n';
    document.head.appendChild(style);
  }


  function setStatus(message, type = "", holdMs = 1000) {
    const status = document.getElementById("sky-status");
    if (!status) return;

    status.textContent = message;
    status.className = `status ${type}`.trim();

    clearTimeout(setStatus.timer);
    if (holdMs > 0) {
      setStatus.timer = setTimeout(() => {
        status.textContent = "Ready";
        status.className = "status";
      }, holdMs);
    }
  }

  function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  async function postCommand(id, element = null) {
    if (element) {
      element.classList.add("active");
      setTimeout(() => element.classList.remove("active"), 110);
    }

    const response = await fetch(`/button/${id}/press`, {
      method: "POST",
      body: "",
      cache: "no-store"
    });

    if (!response.ok) {
      throw new Error(`${id}: HTTP ${response.status}`);
    }
  }

  async function press(id, element = null) {
    if (sequenceBusy) {
      return;
    }

    try {
      await postCommand(id, element);
      setStatus(id.replace("sky_stream_", "").replaceAll("_", " "), "ok");
    } catch (error) {
      console.error("Sky Stream command failed:", error);
      setStatus(`Failed: ${id}`, "error");
    }
  }

  async function runSequence(name, steps, element = null) {
    if (sequenceBusy) {
      return;
    }

    sequenceBusy = true;
    if (element) {
      element.classList.add("active");
    }

    setStatus(name, "", 0);

    try {
      for (const step of steps) {
        const id = typeof step === "string" ? step : step.id;
        setStatus(`${name}: ${id.replace("sky_stream_", "").replaceAll("_", " ")}`, "", 0);
        await postCommand(id);
        await delay(COMMAND_DELAY_MS);
      }

      setStatus(name, "ok");
    } catch (error) {
      console.error(`${name} sequence failed:`, error);
      setStatus(`Failed: ${name}`, "error");
    } finally {
      sequenceBusy = false;
      if (element) {
        element.classList.remove("active");
      }
    }
  }

  function tvGuide(element = null) {
    return runSequence("TV Guide", TV_GUIDE_STEPS, element);
  }

  
  function btn(id, html, classes = "remote-button round", title = "") {
    return `<button
      type="button"
      class="${classes}"
      data-command="${id}"
      aria-label="${title || id}"
      title="${title || ""}">
      ${html}
    </button>`;
  }

  function render() {
    injectStyles();
    document.title = "Sky Stream IR Remote";

    document.body.innerHTML = `
      <main id="sky-app">
        <section class="remote-shell" aria-label="Sky Stream remote control">

          <div class="device-head">
            <i class="device-led"></i>
            <span class="device-host">${location.hostname}</span>
          </div>

          <div class="remote-row three">
            ${btn("sky_stream_power", "⏻", "remote-button round icon", "Power / Standby")}
            ${btn("sky_stream_more", "•••", "remote-button round icon", "Options")}
            ${btn(
              "sky_stream_colour_button",
              '<span class="dots"><i class="dot red"></i><i class="dot yellow"></i><i class="dot green"></i><i class="dot blue"></i></span>',
              "remote-button round icon",
              "Coloured dots"
            )}
          </div>

          <div class="remote-row three" style="margin-top:14px">
            ${btn("sky_stream_back", "↶", "remote-button round icon back-correct", "Back")}
            ${btn(
              "sky_stream_play_pause",
              '<span class="playpause-css"><i class="tri"></i><span class="bars"><i></i><i></i></span></span>',
              "remote-button round icon",
              "Play / Pause"
            )}
            ${btn("sky_stream_mute", "🔇", "remote-button round icon", "Mute")}
          </div>

          <div class="dpad" aria-label="Navigation">
            <button type="button" class="dpad-up" data-command="sky_stream_up" aria-label="Up">⌃</button>
            <button type="button" class="dpad-left" data-command="sky_stream_left" aria-label="Left / Rewind">‹</button>
            <button type="button" class="dpad-ok" data-command="sky_stream_ok" aria-label="OK">OK</button>
            <button type="button" class="dpad-right" data-command="sky_stream_right" aria-label="Right / Fast forward">›</button>
            <button type="button" class="dpad-down" data-command="sky_stream_down" aria-label="Down">⌄</button>
          </div>

          <div class="remote-row lower">
            ${btn("sky_stream_plus", "+", "remote-button round icon plus", "Add to Playlist")}
            ${btn("sky_stream_home", "⌂", "remote-button round icon home", "Home")}

            <div class="remote-button vol" aria-label="Volume">
              <button type="button" class="vol-part" data-command="sky_stream_volume_up" aria-label="Volume up">+</button>
              <span class="vol-label">VOL</span>
              <button type="button" class="vol-part" data-command="sky_stream_volume_down" aria-label="Volume down">−</button>
            </div>
          </div>

          <div class="guide-row">
            <button
              type="button"
              id="tv-guide"
              class="remote-button guide-button"
              aria-label="TV Guide"
              title="TV Guide">
              <span class="guide-icon">▤</span>
              TV Guide
            </button>
          </div>

          <div class="keypad" aria-label="Number pad">
            ${[1,2,3,4,5,6,7,8,9]
              .map(n => `<button type="button" class="remote-button" data-command="sky_stream_${n}">${n}</button>`)
              .join("")}
            <button type="button" class="remote-button zero" data-command="sky_stream_0">0</button>
          </div>

          <div id="sky-status" class="status">Ready</div>
          <div class="footer">ESPHome • Sky Stream IR</div>
        </section>
      </main>
    `;

    document.querySelectorAll("[data-command]").forEach(element => {
      element.addEventListener("click", event => {
        event.preventDefault();
        press(element.dataset.command, element);
      });
    });

    document.getElementById("tv-guide").addEventListener("click", event => {
      event.preventDefault();
      tvGuide(event.currentTarget);
    });

    const keyMap = {
      ArrowUp: "sky_stream_up",
      ArrowDown: "sky_stream_down",
      ArrowLeft: "sky_stream_left",
      ArrowRight: "sky_stream_right",
      Home: "sky_stream_home",
      Enter: "sky_stream_ok",
      Escape: "sky_stream_back",
      Backspace: "sky_stream_back",
      " ": "sky_stream_play_pause",
      "+": "sky_stream_volume_up",
      "=": "sky_stream_volume_up",
      "-": "sky_stream_volume_down",
      "_": "sky_stream_volume_down",
      m: "sky_stream_mute",
      M: "sky_stream_mute",
      "0": "sky_stream_0",
      "1": "sky_stream_1",
      "2": "sky_stream_2",
      "3": "sky_stream_3",
      "4": "sky_stream_4",
      "5": "sky_stream_5",
      "6": "sky_stream_6",
      "7": "sky_stream_7",
      "8": "sky_stream_8",
      "9": "sky_stream_9"
    };

    document.addEventListener("keydown", event => {
      if (event.metaKey || event.ctrlKey || event.altKey || event.repeat) {
        return;
      }

      const id = keyMap[event.key];
      if (!id) {
        return;
      }

      event.preventDefault();

      const element = document.querySelector(`[data-command="${id}"]`);
      press(id, element);
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", render, { once: true });
  } else {
    render();
  }
})();
