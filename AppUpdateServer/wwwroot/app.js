window.aus = {
    copyToClipboard: async (text) => {
        if (!navigator.clipboard) {
            return false;
        }
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
};
