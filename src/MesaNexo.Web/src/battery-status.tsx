import React, { useEffect, useState } from 'react';
import { native } from './bridge';
import { UiIcon } from './ui';

type Source = { onBattery: boolean; source?: 'laptop' | 'ups'; percent?: number | null; minutes?: number | null };
const refreshMs = 30000, lowPercent = 20;
function duration(minutes: number) { const hours = Math.floor(minutes / 60), rest = minutes % 60; return hours ? hours + ' h' + (rest ? ' ' + rest + ' min' : '') : rest + ' min'; }

// Charge left while the CulinaryOS kiosk runs on a laptop battery or UPS. Renders nothing on mains or elsewhere.
export function BatteryStatus() {

    const [power, setPower] = useState<Source>({ onBattery: false });
    useEffect(() => {

        const refresh = () => native<Source>('power.source').then(setPower).catch(() => setPower({ onBattery: false }));
        void refresh();
        const timer = setInterval(refresh, refreshMs);
        return () => clearInterval(timer);

    }, []);
    if (!power.onBattery) return null;
    const device = power.source === 'ups' ? 'UPS' : 'Pila';
    const percent = power.percent == null ? null : power.percent + ' %', time = power.minutes == null ? null : duration(power.minutes);
    const shown = [percent, time].filter(Boolean).join(' · ') || 'En uso';
    const label = (power.source === 'ups' ? 'Funcionando con UPS' : 'Funcionando con pila') + (percent ? ': ' + percent : '') + (time ? ', quedan cerca de ' + time : '');
    const low = power.percent != null && power.percent <= lowPercent;
    return <span className={'battery-status' + (low ? ' low' : '')} role="status" aria-label={label} title={label}><UiIcon name="battery" /><span><b>{device}</b> {shown}</span></span>;

}
