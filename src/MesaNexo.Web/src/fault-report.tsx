import React from 'react';
import { native } from './bridge';

// Where the window was when something failed: the screen and the role, never who.
export const faultContext = { page: '', role: '' };
const reported = new Map<string, number>();
export function reportFault(what: string, affected: string, detail = '') {

    // A render that fails in a loop is one report every few minutes, not thousands.
    const now = Date.now();
    if (now - (reported.get(what) || 0) < 300000) return;
    reported.set(what, now);
    void native('fault.report', { what, affected, detail, context: { ...faultContext, language: navigator.language } }).catch(() => {});

}
const screen = () => faultContext.page ? 'la pantalla ' + faultContext.page : 'el inicio de la aplicación';
// A rejection with a status is an answer of the branch that some screen did not catch; it is not a fault of the app.
export function installFaultReporting() {

    const failed = (event: ErrorEvent) => { if (!/ResizeObserver loop/.test(event.message)) reportFault('Error en la interfaz: ' + event.message, 'Una acción en ' + screen() + ' pudo quedar sin terminar', event.error?.stack || `${event.filename}:${event.lineno}:${event.colno}`); };
    const rejected = (event: PromiseRejectionEvent) => { if (event.reason?.status === undefined) reportFault('Promesa sin atender en la interfaz: ' + (event.reason?.message || event.reason), 'Una acción en ' + screen() + ' pudo quedar sin terminar', event.reason?.stack || ''); };
    window.addEventListener('error', failed);
    window.addEventListener('unhandledrejection', rejected);

}
// Pages render inside App, so a render failure anywhere takes all of it: this sits above and keeps the window usable.
export class FaultBoundary extends React.Component<{ children: React.ReactNode }, { failed: boolean }> {

    state = { failed: false };
    static getDerivedStateFromError() { return { failed: true }; }
    componentDidCatch(error: Error, info: React.ErrorInfo) { reportFault('La interfaz dejó de dibujarse: ' + error.message, 'Quien usaba ' + screen() + ' perdió la pantalla hasta recargarla', (error.stack || '') + (info.componentStack || '')); }
    render() { return this.state.failed ? <div className="fault-screen" role="alert"><h1>Algo falló en esta pantalla</h1><p>Ya avisamos a soporte de Culinary. Los borradores y las operaciones confirmadas se conservan.</p><button type="button" className="ui-button primary" onClick={() => location.reload()}>Recargar la aplicación</button></div> : this.props.children; }

}
