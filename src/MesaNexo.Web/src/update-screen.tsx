import { Button, UiIcon } from './ui';
import { shownVersion } from './client-update';

// The full screen shown while Culinary Restaurant updates. On the CulinaryOS server it follows the update journal through
// the reboot; on the other terminals it follows what the server reported last, and stays while the server restarts; in
// the Windows and Android apps it covers the download and checks before the installer takes over.
export type Progress = { stage: string; active?: boolean; version?: string; reason?: string; at?: string };
const SERVER_STEPS = ['Descargar y comprobar', 'Respaldar la base de datos', 'Instalar', 'Reiniciar', 'Verificar la versión nueva'];
const LOCAL_STEPS = ['Descargar', 'Comprobar el paquete', 'Cerrar sesiones', 'Instalar'];
const serverStep: Record<string, number> = { downloading: 0, received: 0, verified: 0, backup: 1, snapshot: 2, installing: 2, installed: 3, restarting: 3, verifying: 4 };
const localStep: Record<string, number> = { downloading: 0, checking: 1, closing: 2, installing: 3 };
export function UpdateScreen({ progress, mode, close }: { progress: Progress; mode: 'server' | 'remote' | 'local'; close?: () => void }) {

    const steps = mode === 'local' ? LOCAL_STEPS : SERVER_STEPS, map = mode === 'local' ? localStep : serverStep;
    const version = progress.version ? shownVersion(progress.version) : '';
    const ended = ['healthy', 'rolled-back', 'failed'].includes(progress.stage);
    const current = map[progress.stage] ?? 0;
    const title = progress.stage === 'healthy' ? 'Actualización terminada' : progress.stage === 'rolled-back' ? 'Se volvió a la versión anterior' : progress.stage === 'failed' ? 'No se instaló la actualización' : mode === 'remote' ? 'La sucursal se está actualizando' : 'Actualizando Culinary Restaurant';
    const note = progress.stage === 'healthy' ? 'Culinary Restaurant ' + version + ' ya está funcionando.'
        : progress.stage === 'rolled-back' ? 'La versión nueva no arrancó bien y el equipo regresó solo a la que funcionaba. ' + (progress.reason ? 'Motivo: ' + progress.reason + '. ' : '') + 'Tus datos no se tocaron.'
            : progress.stage === 'failed' ? (progress.reason || 'La actualización se detuvo antes de instalar.') + ' El equipo sigue con la versión que tenía.'
                : mode === 'remote' ? 'El servidor de la sucursal instala la versión ' + version + ' y se reinicia. Esta pantalla vuelve sola cuando termine; no apagues el servidor.'
                    : mode === 'server' ? 'No apagues este equipo. Si se va la luz, al volver termina la instalación o regresa a la versión anterior sin perder datos.'
                        : 'Se cierra la aplicación para instalar la versión ' + version + '. Tus borradores se conservan.';
    return <div className="update-screen" role="alertdialog" aria-modal="true" aria-labelledby="update-title" aria-describedby="update-note">
        <div className="update-panel">
            <span className={'update-mark ' + (progress.stage === 'healthy' ? 'ok' : ended ? 'bad' : 'busy')} aria-hidden="true"><UiIcon name={progress.stage === 'healthy' ? 'check' : ended ? 'alert' : 'refresh'} /></span>
            <h1 id="update-title">{title}</h1>
            {version && !ended && <p className="update-version">Versión {version}</p>}
            <p id="update-note" className="update-note">{note}</p>
            {!ended && <ol className="update-steps" aria-label="Avance">{steps.map((label, i) => <li key={label} className={i < current ? 'done' : i === current ? 'current' : ''} aria-current={i === current ? 'step' : undefined}><span>{i < current ? <UiIcon name="check" /> : i + 1}</span>{label}</li>)}</ol>}
            {ended && close && <Button className="primary" icon={false} onClick={close}>Continuar</Button>}
        </div>
    </div>;

}
