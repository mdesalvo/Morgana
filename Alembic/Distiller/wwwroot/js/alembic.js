// Hands a file over: the bytes arrive when the button is pressed and are released once the download starts.
window.alembicSave = (name, base64, type) => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: type || 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    URL.revokeObjectURL(url);
};

// The distillation log reads oldest to newest, so its newest line is kept in view at the bottom.
window.alembicScrollLogBottom = () => {
    const log = document.querySelector('.distillation-log');
    if (log) log.scrollTop = log.scrollHeight;
};
