import React, { useEffect, useLayoutEffect, useRef, useState } from "react";
import { UiIcon } from "./ui";

// With `text`, the trigger is a labelled button ("Configurar") instead of the three-dot icon.
export function ContextMenu({ label, children, text, icon, align = "right" }: { label: string; children: React.ReactNode; text?: string; icon?: string; align?: "left" | "right"; }) {

    const [open, setOpen] = useState(false);
    const root = useRef<HTMLDivElement>(null);
    const trigger = useRef<HTMLButtonElement>(null);
    const panel = useRef<HTMLDivElement>(null);

    useLayoutEffect(() => {

        if (!open) return;
        const place = () => {

            const menu = panel.current;
            if (!menu) return;
            menu.style.translate = 'none';
            menu.classList.remove('up');
            // Open upwards when the menu would leave the bottom of the window and there is room above.
            const below = menu.getBoundingClientRect(), above = (trigger.current?.getBoundingClientRect().top ?? 0) - below.height - 6;
            if (below.bottom > window.innerHeight - 10 && above > 10) menu.classList.add('up');
            const bounds = menu.getBoundingClientRect();
            const shift = Math.max(10 - bounds.left, Math.min(0, window.innerWidth - 10 - bounds.right));
            menu.style.translate = `${shift}px 0`;

        };
        place();
        window.addEventListener('resize', place);
        window.addEventListener('scroll', place, true);
        return () => {

            window.removeEventListener('resize', place);
            window.removeEventListener('scroll', place, true);

        };

    }, [open]);

    useEffect(() => {

        if (!open) return;

        function outside(event: PointerEvent) {

            if (!root.current?.contains(event.target as Node)) setOpen(false);

        }

        document.addEventListener("pointerdown", outside);

        return () => document.removeEventListener("pointerdown", outside);

    }, [open]);

    const controls = () => Array.from(panel.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") || []).filter((button) => button.getClientRects().length);

    return (
        <div className="context-menu" ref={root} onKeyDown={(event) => {

            if (event.key === "Escape" && open) {

                event.preventDefault();
                event.stopPropagation();
                setOpen(false);
                trigger.current?.focus();

            }

            if (!open || !["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;

            event.preventDefault();

            const buttons = controls(), index = buttons.indexOf(document.activeElement as HTMLButtonElement);
            const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : event.key === "ArrowDown" ? (index + 1) % buttons.length : (index - 1 + buttons.length) % buttons.length;
            buttons[next]?.focus();

        }}

        >
            <button ref={trigger} type="button" className={text ? "ui-button ghost menu-trigger" : "context-trigger"} aria-label={text ? undefined : label} title={label} aria-expanded={open} aria-controls={open ? "menu-" + label.replace(/\W/g, "-") : undefined} onClick={() => setOpen(!open)} onKeyDown={(event) => {

                if (event.key === "ArrowDown" && !open) {

                    event.preventDefault();
                    setOpen(true);
                    requestAnimationFrame(() => controls()[0]?.focus());

                }

            }}> {text ? <>{icon && <UiIcon name={icon} />}{text}<UiIcon name="down" className="nav-chevron" /></> : <UiIcon name="dots" />}

            </button> {open && (<div ref={panel} className={"context-options" + (align === "left" ? " left" : "")} role="group" aria-label={label} id={"menu-" + label.replace(/\W/g, "-")} onClick={(event) => {

                const button = (event.target as Element).closest("button");

                if (button && !button.disabled) {

                    setOpen(false);
                    trigger.current?.focus();

                }

            }}

            > {children} </div>
            )} </div>

    );

}
