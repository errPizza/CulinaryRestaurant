import React, { useEffect, useRef, useState } from 'react';

const dialogStack: HTMLElement[] = [];
let unlockedOverflow = '';
function activeDialog() { return dialogStack.filter(element => element.isConnected && !element.closest('.presence.is-leaving')).at(-1); }
function syncDialogLayers() { const active = activeDialog(); for (const element of dialogStack) { element.inert = element !== active; if (element === active) element.removeAttribute('aria-hidden'); else element.setAttribute('aria-hidden', 'true'); } }

// Retain departing content briefly so dialogs and feedback can animate out.
// The wrapper is inert during departure, so an old action cannot be submitted.
export function Presence({ children, className = '' }: { children: React.ReactNode; className?: string }) {

    const retained = useRef(children);
    const visible = Boolean(children);
    const [mounted, setMounted] = useState(visible);
    if (visible) retained.current = children;
    useEffect(() => {

        syncDialogLayers();
        if (visible) { setMounted(true); return; }
        const duration = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 180;
        const timer = window.setTimeout(() => setMounted(false), duration);
        return () => window.clearTimeout(timer);

    }, [visible]);
    if (!visible && !mounted) return null;
    return <div className={`presence ${className} ${visible ? 'is-present' : 'is-leaving'}`}
        inert={!visible} aria-hidden={!visible || undefined}>{children || retained.current}</div>;

}

export function DialogFrame({ children, label, close, busy = false, className = '', placement = 'center' }: {
    children: React.ReactNode; label: string; close: () => void; busy?: boolean; className?: string; placement?: 'center' | 'right';
}) {

    const surface = useRef<HTMLElement>(null);
    const closeRef = useRef(close);
    const busyRef = useRef(busy);
    closeRef.current = close;
    busyRef.current = busy;
    useEffect(() => {

        const previous = document.activeElement as HTMLElement | null;
        const element = surface.current!;
        if (!dialogStack.length) unlockedOverflow = document.body.style.overflow;
        dialogStack.push(element);
        syncDialogLayers();
        document.body.style.overflow = 'hidden';
        const frame = requestAnimationFrame(() => {

            // Do not steal focus after the user has already chosen a field.
            if (activeDialog() !== element || element.contains(document.activeElement)) return;
            const first = surface.current?.querySelector<HTMLElement>('input:not([disabled]),select:not([disabled]),textarea:not([disabled])')
                || surface.current?.querySelector<HTMLElement>('button:not([disabled])');
            (first || surface.current)?.focus();

        });
        function keyboard(event: KeyboardEvent) {

            if (activeDialog() !== element) return;
            if (event.key === 'Escape') {

                event.preventDefault();
                if (!busyRef.current) closeRef.current();

            }
            if (event.key !== 'Tab') return;
            const controls = Array.from(surface.current?.querySelectorAll<HTMLElement>('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href],[tabindex="0"]') || [])
                .filter(element => element.getClientRects().length > 0);
            if (!controls.length) { event.preventDefault(); surface.current?.focus(); return; }
            const first = controls[0], last = controls[controls.length - 1];
            if (event.shiftKey && (document.activeElement === first || !surface.current?.contains(document.activeElement))) {

                event.preventDefault();
                last.focus();

            } else if (!event.shiftKey && (document.activeElement === last || !surface.current?.contains(document.activeElement))) {

                event.preventDefault();
                first.focus();

            }

        }
        document.addEventListener('keydown', keyboard);
        return () => {

            cancelAnimationFrame(frame);
            document.removeEventListener('keydown', keyboard);
            const index = dialogStack.indexOf(element);
            if (index >= 0) dialogStack.splice(index, 1);
            syncDialogLayers();
            if (!dialogStack.length) document.body.style.overflow = unlockedOverflow;
            if (previous?.isConnected && !previous.closest('[inert]')) previous.focus({ preventScroll: true });
            else document.querySelector<HTMLElement>('main h1')?.focus({ preventScroll: true });

        };

    }, []);
    return <div className={'modal-backdrop ' + (placement === 'right' ? 'drawer-backdrop' : '')} onPointerDown={event => { if (placement === 'right' && event.target === event.currentTarget && !busyRef.current && activeDialog() === surface.current) closeRef.current(); }}><section ref={surface} className={'dialog ' + (placement === 'right' ? 'drawer ' : '') + className} tabIndex={-1}
        role="dialog" aria-modal="true" aria-label={label} aria-busy={busy}>{children}</section></div>;

}
