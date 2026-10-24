import { useState } from 'react';

type Props = { id: string; label: string; value: string; onChange: (value: string) => void; required?: boolean; autoComplete?: string; disabled?: boolean; name: string };
export function SecretInput({ id, label, value, onChange, required = false, autoComplete = 'off', disabled = false, name }: Props) {

    const [visible, setVisible] = useState(false);
    return <div className="secret-field"><label htmlFor={id}>{label}</label><div className="secret-control"><input id={id} type={visible ? 'text' : 'password'} value={value} onChange={event => onChange(event.target.value)} required={required} autoComplete={autoComplete} disabled={disabled} spellCheck={false} autoCapitalize="none" /><button type="button" className="secret-toggle" aria-label={(visible ? 'Ocultar ' : 'Mostrar ') + name} aria-controls={id} aria-pressed={visible} title={(visible ? 'Ocultar ' : 'Mostrar ') + name} onClick={() => setVisible(current => !current)}><svg aria-hidden="true" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z" /><circle cx="12" cy="12" r="3" />{visible && <path d="m3 3 18 18" />}</svg></button></div></div>;

}
