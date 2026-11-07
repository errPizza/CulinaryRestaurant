import { useEffect, useRef } from 'react';

// Notification tones synthesized with Web Audio (no audio files) and, on Android, a vibration pattern per event.
// The setting belongs to the device, not to the user: a kitchen screen and a waiter's phone want different things.
export type SoundEvent = 'order' | 'ticket' | 'ready' | 'approved' | 'declined' | 'call' | 'message' | 'error' | 'alert';
export type SoundMode = 'sound' | 'low' | 'vibrate' | 'silent';
export const SOUND_EVENTS: { event: SoundEvent; label: string }[] = [
    { event: 'order', label: 'Pedido nuevo' }, { event: 'ticket', label: 'Comanda en cocina' }, { event: 'ready', label: 'Platillo listo' },
    { event: 'approved', label: 'Pago aprobado' }, { event: 'declined', label: 'Pago rechazado' }, { event: 'call', label: 'Llamada entrante' },
    { event: 'message', label: 'Mensaje' }, { event: 'error', label: 'Error' }, { event: 'alert', label: 'Aviso del sistema' },
];
const KEY = 'culinary.sound.mode';

// The app tells its shells apart by what they inject (Tauri on Windows and the kiosk); none of them can vibrate,
// so only an Android web view, recognized by its user agent, offers «Solo vibrar».
export const isAndroid = () => typeof navigator !== 'undefined' && /Android/i.test(navigator.userAgent);
const canVibrate = () => isAndroid() && typeof navigator.vibrate === 'function';
export function soundModes(): { value: SoundMode; label: string }[] {

    return [{ value: 'sound', label: 'Sonido' }, { value: 'low', label: 'Bajo' }, ...(isAndroid() ? [{ value: 'vibrate' as SoundMode, label: 'Solo vibrar' }] : []), { value: 'silent', label: 'Silencio' }];

}
export function getSoundMode(): SoundMode {

    let v: string | null = null;
    try { v = localStorage.getItem(KEY); } catch { }
    if (v === 'vibrate' && !isAndroid()) return 'sound';
    return v === 'low' || v === 'vibrate' || v === 'silent' ? v : 'sound';

}
export function setSoundMode(mode: SoundMode) { try { localStorage.setItem(KEY, mode); } catch { } }

// [frequency Hz, start s, length s, wave, relative peak, frequency at the end for a glide]
type Note = [number, number, number, OscillatorType?, number?, number?];
const TONES: Record<SoundEvent, Note[]> = {
    order: [[660, 0, 0.14, 'triangle'], [880, 0.17, 0.22, 'triangle']],
    ticket: [[1318, 0, 0.6, 'sine'], [2637, 0, 0.35, 'sine', 0.3]],
    ready: [[784, 0, 0.12, 'triangle'], [988, 0.14, 0.12, 'triangle'], [1175, 0.28, 0.26, 'triangle']],
    approved: [[1047, 0, 0.09, 'sine'], [1568, 0.08, 0.32, 'sine']],
    declined: [[233, 0, 0.5, 'triangle'], [175, 0.12, 0.45, 'sine', 0.6]],
    call: [[880, 0, 0.12, 'sine'], [740, 0.15, 0.12, 'sine'], [880, 0.3, 0.12, 'sine'], [740, 0.45, 0.12, 'sine']],
    message: [[420, 0, 0.12, 'sine', 1, 920]],
    error: [[196, 0, 0.16, 'square', 0.35], [196, 0.24, 0.18, 'square', 0.35]],
    alert: [[660, 0, 0.32, 'sine']],
};
const VIBRATION: Record<SoundEvent, number[]> = {
    order: [120, 80, 120], ticket: [220], ready: [80, 60, 80, 60, 80], approved: [60], declined: [450],
    call: [500, 300, 500], message: [40, 60, 40], error: [250, 100, 250], alert: [150],
};
// Errors and declined payments never fall below a medium level, even on «Bajo».
const VOLUME: Record<'sound' | 'low', number> = { sound: 0.5, low: 0.15 }, URGENT_FLOOR = 0.32;
export const volumeFor = (mode: SoundMode, event: SoundEvent) => mode === 'silent' || mode === 'vibrate' ? 0 : Math.max(VOLUME[mode], event === 'error' || event === 'declined' ? URGENT_FLOOR : 0);

let ctx: AudioContext | null = null;
function audio() {

    if (ctx) return ctx;
    const Ctor = (window as any).AudioContext || (window as any).webkitAudioContext;
    if (!Ctor) return null;
    try { ctx = new Ctor(); } catch { return null; }
    return ctx;

}
// Browsers keep audio suspended until a gesture; the first touch, click or key unlocks it for the rest of the session.
let unlockInstalled = false;
export function installSoundUnlock() {

    if (unlockInstalled) return;
    unlockInstalled = true;
    const unlock = () => {

        const c = audio();
        if (!c) return;
        void c.resume?.().catch(() => { });
        try { const src = c.createBufferSource(); src.buffer = c.createBuffer(1, 1, 22050); src.connect(c.destination); src.start(0); } catch { }
        if (c.state === 'running') for (const name of ['pointerdown', 'keydown', 'touchstart']) window.removeEventListener(name, unlock, true);

    };
    for (const name of ['pointerdown', 'keydown', 'touchstart']) window.addEventListener(name, unlock, true);

}
function synth(event: SoundEvent, volume: number) {

    const c = audio();
    if (!c || volume <= 0) return;
    if (c.state === 'suspended') void c.resume?.().catch(() => { });
    const master = c.createGain(), t0 = c.currentTime + 0.02;
    master.gain.value = volume;
    master.connect(c.destination);
    let end = t0;
    for (const [freq, start, length, wave = 'sine', peak = 1, glide] of TONES[event]) {

        const osc = c.createOscillator(), env = c.createGain(), at = t0 + start;
        osc.type = wave;
        osc.frequency.setValueAtTime(freq, at);
        if (glide) osc.frequency.exponentialRampToValueAtTime(glide, at + length);
        env.gain.setValueAtTime(0.0001, at);
        env.gain.exponentialRampToValueAtTime(peak, at + 0.012);
        env.gain.exponentialRampToValueAtTime(0.0001, at + length);
        osc.connect(env).connect(master);
        osc.start(at);
        osc.stop(at + length + 0.02);
        end = Math.max(end, at + length + 0.05);

    }
    setTimeout(() => { try { master.disconnect(); } catch { } }, (end - c.currentTime) * 1000 + 100);

}
// What actually played, for tests and for support: the last few events only.
const log: { event: SoundEvent; volume: number; vibrate: number[] | null; at: number }[] = [];
(globalThis as any).__culinarySounds = log;
export function playSound(event: SoundEvent, mode = getSoundMode()) {

    if (mode === 'silent') return;
    const volume = volumeFor(mode, event), pattern = canVibrate() ? VIBRATION[event] : null;
    log.push({ event, volume, vibrate: pattern, at: Date.now() });
    if (log.length > 40) log.shift();
    synth(event, volume);
    if (pattern) try { navigator.vibrate(pattern); } catch { }

}

// An incoming call rings every two seconds until someone answers it, twenty seconds at most.
let ringing = 0, ringUntil = 0;
export function startRinging() {

    if (ringing) return;
    ringUntil = Date.now() + 20000;
    playSound('call');
    ringing = window.setInterval(() => { if (Date.now() >= ringUntil) stopRinging(); else playSound('call'); }, 2000);

}
export function stopRinging() { if (ringing) window.clearInterval(ringing); ringing = 0; }
export const isRinging = () => !!ringing;

// «Probar»: each tone once, in order, with its name.
export async function playAllSounds(onEach?: (label: string) => void) {

    const mode = getSoundMode() === 'silent' ? 'sound' : getSoundMode();
    for (const { event, label } of SOUND_EVENTS) { onEach?.(label); playSound(event, mode); await new Promise(r => setTimeout(r, 1100)); }
    onEach?.('');

}

type Row = { id: string;[key: string]: any };
const rows = (s: any, key: string): Row[] => Array.isArray(s?.[key]) ? s[key] : [];
// Every fact that deserves a sound becomes a token; a token never seen before is a new event.
// The tokens are filtered by role so a waiter's phone does not ring for every kitchen ticket in the branch.
export function soundTokens(s: any): Map<string, SoundEvent> {

    const out = new Map<string, SoundEvent>(), u = s?.user || {}, role = u.role, manager = role === 'manager', kitchen = role === 'cocina', waiter = role === 'mesero';
    if (!s) return out;
    const visits = new Map(rows(s, 'visits').map(v => [v.id, v]));
    const mine = (visitId: string) => manager || visits.get(visitId)?.waiterId === u.id;
    if (manager) {

        for (const o of rows(s, 'aggregatororders')) out.set('order:' + o.id, 'order');
        for (const p of rows(s, 'cloudproposals')) if (p.status === 'pending-review') out.set('order:' + p.id, 'order');

    }
    if (manager || kitchen) for (const c of rows(s, 'commands')) out.set('ticket:' + c.id, 'ticket');
    if (manager || waiter) for (const x of rows(s, 'units')) if (x.status === 'ready' && mine(x.visitId)) out.set('ready:' + x.id, 'ready');
    for (const t of rows(s, 'terminaltxns')) {

        if (!(manager || t.actor === u.id)) continue;
        if (t.status === 'approved') out.set('pay:' + t.id + ':approved', 'approved');
        if (t.status === 'declined' || t.status === 'uncertain') out.set('pay:' + t.id + ':' + t.status, 'declined');

    }
    for (const l of rows(s, 'paylinks')) if (l.status === 'paid' && (manager || l.actor === u.id)) out.set('link:' + l.id, 'approved');
    if (manager || waiter) for (const c of rows(s, 'calls')) if (!c.attended) out.set('call:' + c.id, 'call');
    if (manager) {

        const owner = rows(s, 'tickets').find(t => t.channel === 'owner');
        (owner?.messages || []).forEach((m: any, i: number) => { if (m.actor === 'owner') out.set('owner:' + (m.id || i + ':' + m.at), 'message'); });
        for (const j of rows(s, 'printjobs')) if (j.status === 'failed') out.set('print:' + j.id, 'error');
        if (s.updateNotice?.version) out.set('update:' + s.updateNotice.version, 'alert');

    }
    return out;

}
// The events between two snapshots, each kind once. The first snapshot (prev null) never plays.
export function newSoundEvents(prev: Map<string, SoundEvent> | null, next: Map<string, SoundEvent>): SoundEvent[] {

    if (!prev) return [];
    const found = new Set<SoundEvent>();
    for (const [token, event] of next) if (!prev.has(token)) found.add(event);
    return [...found];

}
const PRIORITY: SoundEvent[] = ['call', 'declined', 'error', 'approved', 'order', 'ticket', 'ready', 'message', 'alert'];
/** Plays the sounds for what changed in the state, the connection and the update. One call in the app shell. */
export function useNotificationSounds(s: any, connected: boolean, updateStage?: string) {

    const seen = useRef<Map<string, SoundEvent> | null>(null), user = useRef(''), wasConnected = useRef(false), stage = useRef(updateStage);
    useEffect(() => { installSoundUnlock(); return () => stopRinging(); }, []);
    useEffect(() => {

        const who = s?.user?.id || '';
        if (!s || who !== user.current) { seen.current = null; user.current = who; stopRinging(); }
        if (!s) return;
        const next = soundTokens(s), events = newSoundEvents(seen.current, next);
        seen.current = next;
        events.sort((a, b) => PRIORITY.indexOf(a) - PRIORITY.indexOf(b));
        // Only the most urgent one plays now; two tones on top of each other read as noise.
        const first = events[0];
        if (first === 'call') startRinging(); else if (first) playSound(first);
        if (![...next.values()].includes('call')) stopRinging();

    }, [s]);
    useEffect(() => { if (wasConnected.current && !connected && s) playSound('error'); wasConnected.current = connected; }, [connected]);
    useEffect(() => {

        const before = stage.current;
        stage.current = updateStage;
        if (!before || before === updateStage) return;
        if (updateStage === 'healthy') playSound('alert'); else if (updateStage === 'failed' || updateStage === 'rolled-back') playSound('error');

    }, [updateStage]);

}
