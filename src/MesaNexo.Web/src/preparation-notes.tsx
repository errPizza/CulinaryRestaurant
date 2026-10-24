import React from 'react';

export function PreparationNotes({ notes, cooking, extras = [], modifiers = [] }: { notes?: string; cooking?: string; extras?: ({ name: string } | string)[]; modifiers?: { group?: string; name: string }[] }) {

    return <div className="preparation-notes">
        {(cooking || extras.length > 0 || modifiers.length > 0) && <div className="preparation-tags">{cooking && <span className="preparation-tag cooking-tag">{cooking}</span>}{modifiers.map((m, index) => <span className="preparation-tag modifier-tag" key={'m' + index} title={m.group}>{m.name}</span>)}{extras.map((extra, index) => <span className="preparation-tag" key={index}>+ {typeof extra === 'string' ? extra : extra.name}</span>)}</div>}
        {notes && <div className="preparation-note"><span>Nota del pedido</span><b>{notes}</b></div>}
    </div>;

}
