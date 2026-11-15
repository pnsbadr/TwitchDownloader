// Small dialogs (Shoelace sl-dialog) that return a promise: askText → string or null, confirmAction → true/false.

import { el } from "./core.js";

function dialog(heading, body, confirmLabel, { danger = false, focus } = {}) {
  return new Promise((resolve) => {
    const confirm = el("sl-button", { slot: "footer", variant: danger ? "danger" : "primary" }, confirmLabel);
    const cancel = el("sl-button", { slot: "footer" }, "Cancel");
    const box = el("sl-dialog", { label: heading, class: "small-dialog" }, body, cancel, confirm);
    let result = null;
    const finish = (value) => { result = value; box.hide(); };
    confirm.addEventListener("click", () => finish(true));
    cancel.addEventListener("click", () => finish(false));
    box.addEventListener("sl-after-hide", (event) => {
      if (event.target !== box) return;
      box.remove();
      resolve(result === true);
    });
    box.addEventListener("sl-initial-focus", (event) => {
      if (!focus) return;
      event.preventDefault();
      focus.focus();
    });
    box.finish = finish;
    document.body.append(box);
    customElements.whenDefined("sl-dialog").then(() => box.show());
  });
}

export async function askText({ heading, label, value = "", confirm = "Save", maxLength = 200 }) {
  const input = el("sl-input", { label, value, maxlength: String(maxLength) });
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") input.closest("sl-dialog").finish(true);
  });
  const ok = await dialog(heading, input, confirm, { focus: input });
  return ok ? input.value : null;
}

export function confirmAction({ heading, text, confirm = "OK", danger = true }) {
  return dialog(heading, el("p", {}, text), confirm, { danger });
}
