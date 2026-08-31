(() => {
  "use strict";

  const HOME_SETTLE_MS = 5000;
  const QUICK_MS = 500;
  const AFTER_MENU_DOWN_MS = 3000;
  const AFTER_OK_MS = 3000;
  const AFTER_BACK_MS = 1000;
  const AFTER_LAST_DOWN_MS = 2000;
  const DIGIT_DELAY_MS = 1000;

  function key(id, label) {
    return { type: "press", id, label };
  }

  function wait(ms) {
    return { type: "delay", ms };
  }

  const TV_GUIDE_STEPS = [
    key("sky_stream_home"),
    wait(6000),
    key("sky_stream_down"),
    wait(1000),
    key("sky_stream_down"),
    wait(1000),
    key("sky_stream_ok"),
    wait(4000),
    key("sky_stream_back"),
    wait(3000),
    key("sky_stream_down"),
    wait(4000)
  ];
  const TV_GUIDE_FOOTER = "Locked during sequences";
  const CHANNELS = [
    [101, "BBC One", "Entertainment"],
    [102, "BBC Two", "Entertainment"],
    [103, "ITV1", "Entertainment"],
    [104, "Channel 4", "Entertainment"],
    [105, "Channel 5", "Entertainment"],
    [106, "Sky One", "Entertainment"],
    [107, "Sky Witness", "Entertainment"],
    [108, "Sky Atlantic", "Entertainment"],
    [109, "Sky Comedy", "Entertainment"],
    [110, "Sky Documentaries", "Entertainment"],
    [111, "Sky Crime", "Entertainment"],
    [112, "Sky Arts", "Entertainment"],
    [113, "Sky Nature", "Entertainment"],
    [114, "Sky Sci-Fi", "Entertainment"],
    [115, "Sky History", "Entertainment"],
    [118, "BBC Three", "Entertainment"],
    [119, "BBC Four", "Entertainment"],
    [120, "U&Alibi", "Entertainment"],
    [121, "U&Gold", "Entertainment"],
    [122, "ITV2", "Entertainment"],
    [123, "ITV3", "Entertainment"],
    [124, "ITV4", "Entertainment"],
    [125, "ITV Quiz", "Entertainment"],
    [126, "E4", "Entertainment"],
    [127, "More4", "Entertainment"],
    [128, "5STAR", "Entertainment"],
    [129, "5USA", "Entertainment"],
    [130, "U&Dave", "Entertainment"],
    [131, "U&W", "Entertainment"],
    [132, "U&Drama", "Entertainment"],
    [133, "U&Yesterday", "Entertainment"],
    [134, "U&Eden", "Entertainment"],
    [135, "Comedy Central", "Entertainment"],
    [136, "Comedy Central Extra", "Entertainment"],
    [137, "MTV", "Entertainment"],
    [138, "Discovery", "Entertainment"],
    [139, "TLC", "Entertainment"],
    [140, "Investigation Discovery", "Entertainment"],
    [141, "Animal Planet", "Entertainment"],
    [142, "Crime + Investigation", "Entertainment"],
    [143, "Sky History 2", "Entertainment"],
    [144, "National Geographic", "Entertainment"],
    [145, "Nat Geo Wild", "Entertainment"],
    [146, "Discovery Turbo", "Entertainment"],
    [147, "Discovery History", "Entertainment"],
    [148, "Discovery Science", "Entertainment"],
    [149, "Quest", "Entertainment"],
    [150, "Quest Red", "Entertainment"],
    [151, "DMAX", "Entertainment"],
    [152, "Food Network", "Entertainment"],
    [153, "Really", "Entertainment"],
    [155, "True Crime", "Entertainment"],
    [156, "Legend", "Entertainment"],
    [157, "True Crime Xtra", "Entertainment"],
    [158, "Sky Mix", "Entertainment"],
    [159, "Challenge", "Entertainment"],
    [163, "4seven", "Entertainment"],
    [164, "E4 Extra", "Entertainment"],
    [165, "5ACTION", "Entertainment"],
    [166, "5SELECT", "Entertainment"],
    [167, "GREAT! TV", "Entertainment"],
    [169, "Blaze", "Entertainment"],
    [170, "PBS America", "Entertainment"],
    [171, "Together TV", "Entertainment"],
    [172, "S4C", "Entertainment"],
    [173, "BBC Scotland", "Entertainment"],
    [174, "BBC Alba", "Entertainment"],
    [201, "CBBC", "Kids"],
    [202, "CBeebies", "Kids"],
    [203, "Sky Kids", "Kids"],
    [204, "Disney Jr", "Kids"],
    [205, "Nickelodeon", "Kids"],
    [206, "Nicktoons", "Kids"],
    [207, "Nick Jr", "Kids"],
    [208, "Nick Jr Too", "Kids"],
    [209, "Cartoon Network", "Kids"],
    [210, "Boomerang", "Kids"],
    [211, "Cartoonito", "Kids"],
    [212, "BabyTV", "Kids"],
    [301, "Sky Cinema Premiere", "Movies"],
    [302, "Sky Cinema Animation", "Movies"],
    [303, "Sky Cinema Box Set", "Movies"],
    [304, "Sky Cinema Family", "Movies"],
    [305, "Disney+ Cinema", "Movies"],
    [306, "Sky Cinema Action", "Movies"],
    [307, "Sky Cinema Greats", "Movies"],
    [308, "Sky Cinema Comedy", "Movies"],
    [309, "Sky Cinema Thriller", "Movies"],
    [310, "Sky Cinema Drama", "Movies"],
    [311, "Sky Cinema Sci-Fi/Horror", "Movies"],
    [312, "Film4", "Movies"],
    [313, "Movies24", "Movies"],
    [314, "Movies24+", "Movies"],
    [316, "Legend Xtra", "Movies"],
    [317, "GREAT! Action", "Movies"],
    [318, "GREAT! Mystery", "Movies"],
    [319, "GREAT! Romance", "Movies"],
    [354, "Clubland TV", "Music"],
    [355, "NOW 70s", "Music"],
    [356, "NOW 80s", "Music"],
    [357, "NOW 90s & 00s", "Music"],
    [358, "NOW Rock", "Music"],
    [401, "Sky Sports Main Event", "Sports"],
    [402, "Sky Sports Premier League", "Sports"],
    [403, "Sky Sports Football", "Sports"],
    [404, "Sky Sports+", "Sports"],
    [405, "Sky Sports Cricket", "Sports"],
    [406, "Sky Sports Golf", "Sports"],
    [407, "Sky Sports F1", "Sports"],
    [408, "Sky Sports Tennis", "Sports"],
    [409, "Sky Sports News", "Sports"],
    [410, "Sky Sports Action", "Sports"],
    [411, "Sky Sports Racing", "Sports"],
    [412, "Sky Sports Mix", "Sports"],
    [413, "TNT Sports 1", "Sports"],
    [414, "TNT Sports 2", "Sports"],
    [415, "TNT Sports 3", "Sports"],
    [416, "TNT Sports 4", "Sports"],
    [417, "GINX eSports", "Sports"],
    [418, "MUTV", "Sports"],
    [419, "LFCTV", "Sports"],
    [493, "TNT Sports Ultimate", "Sports"],
    [501, "Sky News", "News"],
    [502, "BBC News", "News"],
    [503, "BBC Parliament", "News"],
    [504, "CNBC", "News"],
    [505, "Bloomberg", "News"],
    [506, "CNN", "News"],
    [508, "TalkTV", "News"],
    [509, "GB News", "News"],
    [510, "Euronews", "News"],
    [511, "NDTV 24x7", "News"],
    [512, "France 24", "News"],
    [601, "QVC", "Shopping"],
    [602, "QVC Style", "Shopping"],
    [603, "QVC Beauty", "Shopping"],
    [604, "QVC Extra", "Shopping"],
    [701, "Star Bharat", "International"],
    [702, "Star Plus", "International"],
    [703, "Star Gold", "International"],
    [704, "Sony TV", "International"],
    [705, "Sony MAX", "International"],
    [706, "Sony SAB", "International"],
    [707, "Sony MAX 2", "International"],
    [708, "Colors", "International"],
    [709, "Colors Rishtey", "International"],
    [710, "Colors Gujarati", "International"],
    [711, "B4U Movies", "International"],
    [712, "B4U Music", "International"],
    [713, "Zee Cinema", "International"],
    [714, "Zee TV", "International"],
    [716, "Sky News Arabia", "International"],
    [720, "HUM News", "International"],
    [721, "HUM Europe", "International"],
    [722, "Geo News", "International"],
    [723, "Geo TV", "International"],
    [724, "ARY Digital", "International"],
    [725, "New Vision", "International"],
    [740, "PTC Punjabi", "International"],
    [741, "Zee Punjabi", "International"],
    [742, "Brit Asia", "International"]
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

  function injectLiveTvStyles() {
    if (document.getElementById("sky-live-tv-css")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "sky-live-tv-css";
    style.textContent = `
.live-tv {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 226px;
  margin: 0 auto 14px;
}
.live-tv-label {
  text-align: center;
  font-size: 11px;
  font-weight: 750;
  letter-spacing: .4px;
  color: var(--muted);
}
.live-tv-search,
.live-tv-select {
  appearance: none;
  width: 100%;
  border: 2px solid #050708;
  border-radius: 14px;
  background: linear-gradient(150deg, var(--button), var(--button-low));
  color: var(--text);
  font: 650 13px/1.3 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  padding: 10px 12px;
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.11),
    0 4px 8px rgba(0,0,0,.38);
}
.live-tv-search::placeholder {
  color: var(--muted);
  font-weight: 500;
}
.live-tv-search:focus,
.live-tv-select:focus {
  outline: 1px solid rgba(23,140,255,.55);
}
.live-tv-select {
  cursor: pointer;
}
.live-tv-search:disabled,
.live-tv-select:disabled {
  opacity: .55;
  cursor: not-allowed;
}
.live-tv-select option,
.live-tv-select optgroup {
  background: #161b20;
  color: var(--text);
}
.sequence-hud {
  margin: 0 0 16px;
  padding: 12px 12px 11px;
  border: 2px solid #050708;
  border-radius: 18px;
  background:
    linear-gradient(180deg, rgba(57,229,109,.08), transparent 42%),
    linear-gradient(180deg, #1c2833, #12181d);
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.08),
    0 6px 14px rgba(0,0,0,.28);
}
.sequence-labels {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
  margin-bottom: 10px;
}
.sequence-kicker {
  display: block;
  font-size: 9px;
  letter-spacing: .8px;
  text-transform: uppercase;
  color: var(--muted);
  margin-bottom: 3px;
}
.sequence-labels strong {
  display: block;
  font-size: 15px;
  font-weight: 800;
  letter-spacing: .2px;
  text-transform: capitalize;
  line-height: 1.15;
}
#sequence-now { color: var(--green); }
#sequence-next { color: #9be7ff; }
.sequence-track {
  height: 9px;
  border-radius: 99px;
  background: #0b1014;
  border: 1px solid #050708;
  overflow: hidden;
}
.sequence-bar {
  display: block;
  width: 100%;
  height: 100%;
  transform: scaleX(1);
  transform-origin: left center;
  border-radius: inherit;
  background: linear-gradient(90deg, #39e56d, #178cff);
  box-shadow: 0 0 12px rgba(57,229,109,.4);
  transition: none;
}
.sequence-caption {
  margin-top: 8px;
  text-align: center;
  font-size: 10px;
  color: var(--muted);
}
.remote-shell.is-locked {
  cursor: wait;
}
.remote-shell.is-locked .device-led {
  background: var(--yellow);
  box-shadow: 0 0 8px rgba(255,209,47,.7);
}
.remote-shell.is-locked button,
.remote-shell.is-locked input,
.remote-shell.is-locked select {
  pointer-events: none !important;
  opacity: .42;
}
.remote-shell.is-locked button.active {
  opacity: 1;
}
`;
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

  function prettyLabel(id) {
    return String(id).replace("sky_stream_", "").split("_").join(" ");
  }

  function sleep(ms) {
    return new Promise(resolve => window.setTimeout(resolve, ms));
  }

  function postCommand(id, element = null, signal = undefined) {
    if (element) {
      element.classList.add("active");
      setTimeout(() => element.classList.remove("active"), 110);
    }

    return fetch("/button/" + id + "/press", { method: "POST", cache: "no-store", signal });
  }

  function sequenceEls() {
    return {
      hud: document.getElementById("sequence-hud"),
      now: document.getElementById("sequence-now"),
      next: document.getElementById("sequence-next"),
      nextWrap: document.getElementById("sequence-next-wrap"),
      bar: document.getElementById("sequence-bar"),
      caption: document.getElementById("sequence-caption"),
      shell: document.querySelector(".remote-shell")
    };
  }

  function resetCountdownBar() {
    const { bar } = sequenceEls();
    if (!bar) {
      return;
    }
    bar.style.transition = "none";
    bar.style.transform = "scaleX(1)";
  }

  function stepName(step) {
    if (!step) {
      return "";
    }
    if (step.type === "delay") {
      return (step.ms / 1000).toFixed(1) + "s wait";
    }
    return step.label || prettyLabel(step.id);
  }

  function showSequenceHud(nowText, nextText, caption) {
    const { hud, now, next, nextWrap, caption: captionEl } = sequenceEls();
    if (!hud) {
      return;
    }
    hud.hidden = false;
    now.textContent = nowText;
    if (nextText) {
      nextWrap.hidden = false;
      next.textContent = nextText;
    } else {
      nextWrap.hidden = true;
      next.textContent = "";
    }
    captionEl.textContent = caption;
  }

  function hideSequenceHud() {
    const { hud } = sequenceEls();
    if (hud) {
      hud.hidden = true;
    }
    resetCountdownBar();
  }

  function setUiLocked(locked) {
    sequenceBusy = locked;
    const { shell } = sequenceEls();
    if (shell) {
      shell.classList.toggle("is-locked", locked);
      shell.setAttribute("aria-busy", locked ? "true" : "false");
    }
    setLiveTvDisabled(locked);
    if (locked && document.activeElement && document.activeElement.blur) {
      document.activeElement.blur();
    }
    if (!locked) {
      hideSequenceHud();
    }
  }

  async function countdownBar(ms, nextName) {
    const { bar, caption: captionEl } = sequenceEls();
    const started = performance.now();

    if (bar) {
      bar.style.transition = "none";
      bar.style.transform = "scaleX(1)";
    }

    while (true) {
      const elapsed = performance.now() - started;
      const remainingMs = Math.max(0, ms - elapsed);
      if (bar) {
        bar.style.transform = "scaleX(" + (ms > 0 ? remainingMs / ms : 0) + ")";
      }

      const caption = nextName
        ? "Wait " + (remainingMs / 1000).toFixed(1) + "s then " + nextName
        : "Wait " + (remainingMs / 1000).toFixed(1) + "s";
      if (captionEl) {
        captionEl.textContent = caption;
      }
      setStatus(caption, "", 0);

      if (elapsed >= ms) {
        break;
      }

      await sleep(Math.min(40, Math.max(remainingMs, 1)));
    }
  }

  async function sendAndWait(id) {
    const element = document.querySelector(`[data-command="${id}"]`);
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), 8000);
    try {
      const response = await postCommand(id, element, controller.signal);
      if (!response.ok) {
        throw new Error(prettyLabel(id) + " HTTP " + response.status);
      }
      await response.text();
    } catch (error) {
      if (error && error.name === "AbortError") {
        throw new Error(prettyLabel(id) + " timed out");
      }
      throw error;
    } finally {
      window.clearTimeout(timer);
    }
  }

  async function runSequence(steps, doneMessage) {
    if (sequenceBusy) {
      return;
    }

    setUiLocked(true);
    try {
      for (let index = 0; index < steps.length; index++) {
        const step = steps[index];
        const name = stepName(step);
        const nextName = stepName(steps[index + 1]);

        if (step.type === "delay") {
          showSequenceHud(name, nextName, "Wait");
          setStatus("Wait", "", 0);
          await countdownBar(step.ms, nextName);
          continue;
        }

        if (step.type !== "press" || !step.id) {
          throw new Error("Unknown sequence step");
        }

        showSequenceHud(name, nextName, "Sending " + name);
        setStatus("Sending " + name, "", 0);
        await sendAndWait(step.id);
      }
      setStatus(doneMessage, "ok");
    } catch (error) {
      console.error("Sequence failed:", error);
      setStatus("Failed: " + (error && error.message ? error.message : error), "error", 8000);
    } finally {
      setUiLocked(false);
    }
  }

  async function press(id, element = null) {
    if (sequenceBusy) {
      return;
    }

    sequenceBusy = true;
    try {
      const response = await postCommand(id, element);
      if (!response.ok) {
        throw new Error(prettyLabel(id) + " HTTP " + response.status);
      }
      setStatus(prettyLabel(id), "ok");
    } catch (error) {
      console.error("Sky Stream command failed:", error);
      setStatus("Failed: " + (error && error.message ? error.message : id), "error", 5000);
    } finally {
      sequenceBusy = false;
    }
  }

  function tvGuide() {
    return runSequence(TV_GUIDE_STEPS, "TV Guide");
  }

  function setLiveTvDisabled(disabled) {
    const search = document.getElementById("live-tv-search");
    const select = document.getElementById("live-tv-select");
    if (search) {
      search.disabled = disabled;
    }
    if (select) {
      select.disabled = disabled;
    }
  }

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function channelOptionsHtml(filter) {
    const query = String(filter || "").trim().toLowerCase();
    const groups = [];
    const indexByCat = {};

    CHANNELS.forEach(([number, name, category]) => {
      if (query && !name.toLowerCase().includes(query) && !String(number).includes(query)) {
        return;
      }

      let groupIndex = indexByCat[category];
      if (groupIndex == null) {
        groupIndex = groups.length;
        indexByCat[category] = groupIndex;
        groups.push({ category, channels: [] });
      }

      groups[groupIndex].channels.push([number, name]);
    });

    let html = '<option value="">Live TV…</option>';
    groups.forEach(({ category, channels }) => {
      html += `<optgroup label="${escapeHtml(category)}">`;
      channels.forEach(([number, name]) => {
        html += `<option value="${number}">${number} ${escapeHtml(name)}</option>`;
      });
      html += "</optgroup>";
    });
    return html;
  }

  async function tuneChannel(number) {
    if (sequenceBusy) {
      return;
    }

    const digits = String(number);
    if (!/^[1-9]\d{0,3}$/.test(digits)) {
      return;
    }

    const steps = TV_GUIDE_STEPS.slice();
    [...digits].forEach(digit => {
      steps.push(key("sky_stream_" + digit));
      steps.push(wait(DIGIT_DELAY_MS));
    });
    steps.push(key("sky_stream_ok", "ok 1"));
    steps.push(wait(AFTER_OK_MS));
    steps.push(key("sky_stream_ok", "ok 2"));
    steps.push(wait(AFTER_OK_MS));

    await runSequence(steps, "Live TV " + digits);

    const select = document.getElementById("live-tv-select");
    if (select) {
      select.value = "";
    }
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
    injectLiveTvStyles();
    document.title = "Sky Stream IR Remote";

    document.body.innerHTML = `
      <main id="sky-app">
        <section class="remote-shell" aria-label="Sky Stream remote control">

          <div class="device-head">
            <i class="device-led"></i>
            <span class="device-host">${location.hostname}</span>
          </div>

          <div id="sequence-hud" class="sequence-hud" hidden>
            <div class="sequence-labels">
              <div>
                <span class="sequence-kicker">Now</span>
                <strong id="sequence-now"></strong>
              </div>
              <div id="sequence-next-wrap">
                <span class="sequence-kicker">Next</span>
                <strong id="sequence-next"></strong>
              </div>
            </div>
            <div class="sequence-track" aria-hidden="true">
              <i id="sequence-bar" class="sequence-bar"></i>
            </div>
            <div id="sequence-caption" class="sequence-caption"></div>
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

          <div class="live-tv">
            <div class="live-tv-label">Live TV</div>
            <input
              type="search"
              id="live-tv-search"
              class="live-tv-search"
              placeholder="Find channel"
              autocomplete="off"
              spellcheck="false"
              aria-label="Search channels">
            <select id="live-tv-select" class="live-tv-select" aria-label="Live TV">
              ${channelOptionsHtml("")}
            </select>
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
          <div class="footer">${TV_GUIDE_FOOTER}</div>
        </section>
      </main>
    `;

    document.querySelectorAll("[data-command]").forEach(element => {
      element.addEventListener("click", event => {
        event.preventDefault();
        press(element.dataset.command, element);
      });
    });

    document.querySelector(".remote-shell").addEventListener("click", event => {
      if (!sequenceBusy) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
    }, true);

    document.getElementById("tv-guide").addEventListener("click", event => {
      event.preventDefault();
      tvGuide();
    });

    const liveTvSearch = document.getElementById("live-tv-search");
    const liveTvSelect = document.getElementById("live-tv-select");

    liveTvSearch.addEventListener("input", () => {
      const current = liveTvSelect.value;
      liveTvSelect.innerHTML = channelOptionsHtml(liveTvSearch.value);
      if ([...liveTvSelect.options].some(option => option.value === current)) {
        liveTvSelect.value = current;
      }
    });

    liveTvSelect.addEventListener("change", () => {
      if (!liveTvSelect.value) {
        return;
      }
      tuneChannel(liveTvSelect.value);
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
      if (event.metaKey || event.ctrlKey || event.altKey) {
        return;
      }

      if (sequenceBusy) {
        event.preventDefault();
        return;
      }

      if (event.repeat) {
        return;
      }

      if (event.target && /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName)) {
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
