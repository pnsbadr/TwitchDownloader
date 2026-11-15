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

// Someone else's item that this member may edit (EditAnyEntries): change it, or change a copy on their own row.
// Resolves "copy" (the default: Enter), "original", or null when cancelled.
export function chooseEditTarget({ what, owner }) {
  return new Promise((resolve) => {
    const copy = el("sl-button", { slot: "footer", variant: "primary" }, "Make a copy");
    const original = el("sl-button", { slot: "footer" }, "Edit original");
    const cancel = el("sl-button", { slot: "footer", variant: "text" }, "Cancel");
    const box = el("sl-dialog", { label: `${owner ? `${owner}'s` : "Someone else's"} ${what}`, class: "small-dialog" },
      el("p", {}, `Change the original (everyone sees the change), or make a copy on your own row and change that?`),
      cancel, original, copy);
    let result = null;
    const finish = (value) => { result = value; box.hide(); };
    copy.addEventListener("click", () => finish("copy"));
    original.addEventListener("click", () => finish("original"));
    cancel.addEventListener("click", () => finish(null));
    // Enter = Make a copy, wherever the focus is (unless it's on the other buttons)
    const onKey = (event) => {
      if (event.key !== "Enter" || result !== null) return;
      const path = event.composedPath();
      if (path.includes(original) || path.includes(cancel)) return;
      event.preventDefault();
      event.stopPropagation();
      finish("copy");
    };
    document.addEventListener("keydown", onKey, true);
    box.addEventListener("sl-initial-focus", (event) => {
      event.preventDefault();
      customElements.whenDefined("sl-button").then(() => copy.focus());
    });
    box.addEventListener("sl-after-hide", (event) => {
      if (event.target !== box) return;
      document.removeEventListener("keydown", onKey, true);
      box.remove();
      resolve(result);
    });
    document.body.append(box);
    customElements.whenDefined("sl-dialog").then(() => box.show());
  });
}
