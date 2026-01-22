export const GetNPView = function (params?: any): HTMLElement {
    const element =
        document.querySelector<HTMLElement>('[data-test="now-playing"]') ||
        document.getElementById('nowPlaying') ||
        document.querySelector<HTMLElement>('section[class*="_nowPlayingContainer"]');

    if (!element) {
        throw new Error('bleh');
    }

    return element;
};
