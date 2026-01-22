/**
 * Audio Visualiser Module
 */

import { currentDevice } from ".";

export interface AudioAnalysis {
    bass?: {
        strongest: {
            frequency: number;
            magnitude: number;
        };
        average: number;
        max: number;
        frequency: number;
    };
    bpm?: number;
    utime?: number | bigint;
}

export interface AudioVisualiserOptions {
    wsUrl?: string;
    autoReconnect?: boolean;
    maxReconnectAttempts?: number;
    reconnectDelay?: number;
    lerpFactor?: number;
    showStats?: boolean;
    showStatus?: boolean;
    zIndex?: number;
    isNowPlayingVisible?: boolean;
}

export interface AudioVisualiserAPI {
    destroy: () => void;
    reconnect: () => void;
    disconnect: () => void;
    isConnected: () => boolean;
    setLerpFactor: (factor: number) => void;
    togglePause: (pause: boolean) => void;
    deviceChanged: (deviceId: string) => void;
}

export class AudioVisualiser implements AudioVisualiserAPI {
    private overlayWrapper: HTMLElement;
    private container: HTMLElement;
    public ws: WebSocket | null = null;
    private reconnectTimeout: number | null = null;
    private reconnectAttempts = 0;

    private elements: {
        vignette: HTMLElement;
        glowLayer: HTMLElement;
        pulseRing: HTMLElement;
    };

    private state = {
        currentVignetteSize: 100,
        currentVignetteBlur: 10,
        currentIntensity: 0,
        targetVignetteSize: 100,
        targetVignetteBlur: 10,
        targetIntensity: 0,
    };

    private options: Required<AudioVisualiserOptions>;

    constructor(
        containerSelector: string | HTMLElement,
        options: AudioVisualiserOptions = {}
    ) {
        this.options = {
            wsUrl: 'ws://localhost:5343',
            autoReconnect: true,
            maxReconnectAttempts: 5,
            reconnectDelay: 2000,
            lerpFactor: 0.5,
            showStats: false,
            showStatus: false,
            zIndex: -1,
            isNowPlayingVisible: false,
            ...options,
        };

        this.container = this.resolveContainer(containerSelector);
        this.ensureContainerPosition();
        this.overlayWrapper = this.createOverlayWrapper();
        this.elements = this.createDOMStructure();
        this.connect();
    }

    // Resolves container from selector or element
    private resolveContainer(selector: string | HTMLElement): HTMLElement {
        if (typeof selector === 'string') {
            const element = document.querySelector<HTMLElement>(selector);
            if (!element) {
                throw new Error(`Container not found: ${selector}`);
            }
            return element;
        }
        return selector;
    }

    private ensureContainerPosition(): void {
        const position = window.getComputedStyle(this.container).position;
        if (position === 'static') {
            this.container.style.position = 'relative';
        }
    }

    private createOverlayWrapper(): HTMLElement {
        const wrapper = document.createElement('div');
        wrapper.style.cssText = `
            position: absolute !important;
            top: 0 !important;
            left: 0 !important;
            width: 100% !important;
            height: 100% !important;
            overflow: hidden !important;
            pointer-events: none !important;
            z-index: ${this.options.zIndex} !important;
        `;
        this.container.appendChild(wrapper);
        return wrapper;
    }

    private createDOMStructure() {
        const visualiser = document.createElement('div');
        visualiser.style.cssText = `
            position: absolute !important;
            top: 0 !important;
            left: 0 !important;
            width: 100% !important;
            height: 100% !important;
            background: transparent !important;
        `;

        const glowLayer = document.createElement('div');
        glowLayer.style.cssText = `
            position: absolute !important;
            top: 0 !important;
            left: 0 !important;
            width: 100% !important;
            height: 100% !important;
            pointer-events: none !important;
            transition: background 0.08s ease-out !important;
        `;

        const vignette = document.createElement('div');
        vignette.style.cssText = `
            position: absolute !important;
            top: 0 !important;
            left: 0 !important;
            width: 100% !important;
            height: 100% !important;
            pointer-events: none !important;
            opacity: 0.2 !important;
            transition: box-shadow 0.08s cubic-bezier(0.4, 0.0, 0.2, 1) !important;
        `;

        const pulseRing = document.createElement('div');
        pulseRing.style.cssText = `
            position: absolute !important;
            top: 50% !important;
            left: 50% !important;
            transform: translate(-50%, -50%) !important;
            width: 300px !important;
            height: 300px !important;
            border-radius: 50% !important;
            transition: all 0.08s ease-out !important;
            opacity: 0 !important;
            pointer-events: none !important;
        `;

        const status = document.createElement('div');
        status.textContent = 'Disconnected';
        status.style.cssText = `
            position: absolute !important;
            top: 20px !important;
            right: 20px !important;
            padding: 8px 16px !important;
            border-radius: 20px !important;
            font-size: 12px !important;
            font-weight: 500 !important;
            backdrop-filter: blur(10px) !important;
            pointer-events: all !important;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif !important;
            background: rgba(255, 50, 50, 0.15) !important;
            color: #ff3232 !important;
            border: 1px solid rgba(255, 50, 50, 0.3) !important;
            display: ${this.options.showStatus ? 'block' : 'none'} !important;
            z-index: ${this.options.zIndex} !important;
        `;

        const stats = document.createElement('div');
        stats.style.cssText = `
            position: absolute !important;
            bottom: 30px !important;
            left: 50% !important;
            transform: translateX(-50%) !important;
            display: ${this.options.showStats ? 'flex' : 'none'} !important;
            gap: 20px !important;
            z-index: ${this.options.zIndex} !important;
            pointer-events: all !important;
        `;

        const createStat = (label: string) => {
            const stat = document.createElement('div');
            stat.style.cssText = `
                background: rgba(255, 255, 255, 0.05) !important;
                backdrop-filter: blur(10px) !important;
                padding: 12px 20px !important;
                border-radius: 12px !important;
                border: 1px solid rgba(255, 255, 255, 0.1) !important;
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif !important;
            `;

            const statLabel = document.createElement('div');
            statLabel.textContent = label;
            statLabel.style.cssText = `
                font-size: 11px !important;
                color: rgba(255, 255, 255, 0.5) !important;
                text-transform: uppercase !important;
                letter-spacing: 1px !important;
                margin-bottom: 4px !important;
            `;

            const statValue = document.createElement('div');
            statValue.textContent = '0';
            statValue.style.cssText = `
                font-size: 20px !important;
                font-weight: 600 !important;
                color: #fff !important;
            `;

            stat.appendChild(statLabel);
            stat.appendChild(statValue);

            return { stat, value: statValue };
        };

        const bassStat = createStat('Bass');
        const freqStat = createStat('Frequency');
        const bpmStat = createStat('BPM');

        stats.appendChild(bassStat.stat);
        stats.appendChild(freqStat.stat);
        stats.appendChild(bpmStat.stat);

        visualiser.appendChild(glowLayer);
        visualiser.appendChild(vignette);
        visualiser.appendChild(pulseRing);

        this.overlayWrapper.appendChild(visualiser);
        this.overlayWrapper.appendChild(status);
        this.overlayWrapper.appendChild(stats);

        return {
            vignette,
            glowLayer,
            pulseRing,
            status,
            stats,
            bassValue: bassStat.value,
            freqValue: freqStat.value,
            bpmValue: bpmStat.value,
        };
    }

    /**
     * Linear interpolation for smooth transitions
     */
    private lerp(start: number, end: number, factor: number): number {
        return start + (end - start) * factor;
    }

    // ws connection
    private connect(): void {
        try {
            this.ws = new WebSocket(this.options.wsUrl);

            this.ws.onopen = () => {
                this.ws?.send(currentDevice);
            };

            this.ws.onmessage = (event: MessageEvent) => {
                try {
                    const data: AudioAnalysis[] = JSON.parse(event.data);
                    if (Array.isArray(data) && data.length > 0) {
                        this.update(data[0]);
                    } else {
                        console.log("Invalid data format:", data);
                    }
                } catch (error) {
                    // Silently handle parsing errors
                }
            };

            this.ws.onerror = () => {
                // Connection error occurred
            };

            this.ws.onclose = () => {
                // If explicitly disconnected, don't reconnect
                if (!this.ws) return;

                // this.elements.status.textContent = 'Disconnected';
                // this.elements.status.style.background = 'rgba(255, 50, 50, 0.15)';
                // this.elements.status.style.color = '#ff3232';

                if (this.options.autoReconnect) {
                    this.scheduleReconnect();
                }
            };
        } catch (error) {
            this.scheduleReconnect();
        }
    }

    public deviceChanged(deviceId: string): void {
        if (this.ws) {
            this.ws?.send(deviceId);
        }
    }

    private scheduleReconnect(): void {
        if (this.reconnectAttempts >= this.options.maxReconnectAttempts) {
            return;
        }

        this.reconnectAttempts++;
        const delay = this.options.reconnectDelay * this.reconnectAttempts;

        this.reconnectTimeout = window.setTimeout(() => {
            this.connect();
        }, delay);
    }

    public togglePause(pause: boolean): void {
        this.options.isNowPlayingVisible = pause;
    }

    /**
     * Visualiser update
     */
    private update(analysis: AudioAnalysis): void {
        if (!this.options.isNowPlayingVisible) {
            return;
        }

        const strongestBass = analysis.bass?.strongest;
        const bassAverage = analysis.bass?.average || 0;

        if (!strongestBass) {
            return;
        }

        // Calculate intensity based on frequency and magnitude
        const lowFreqIntensity = Math.max(0, 1 - (strongestBass.frequency - 20) / 200);
        const magnitudeIntensity = Math.min(bassAverage * 10000, 1);
        const totalIntensity = lowFreqIntensity * magnitudeIntensity;

        // Update target values
        this.state.targetVignetteSize = 100 + totalIntensity * 300;
        this.state.targetVignetteBlur = 10 + totalIntensity * 200;
        this.state.targetIntensity = totalIntensity;

        // Apply smooth interpolation
        const { lerpFactor } = this.options;
        this.state.currentVignetteSize = this.lerp(
            this.state.currentVignetteSize,
            this.state.targetVignetteSize,
            lerpFactor
        );
        this.state.currentVignetteBlur = this.lerp(
            this.state.currentVignetteBlur,
            this.state.targetVignetteBlur,
            lerpFactor
        );
        this.state.currentIntensity = this.lerp(
            this.state.currentIntensity,
            this.state.targetIntensity,
            lerpFactor
        );

        this.applyEffects(strongestBass.frequency);
    }

    /**
    * Applies visual effects based on current state
    */
    private applyEffects(frequency: number): void {
        const { currentVignetteSize, currentVignetteBlur, currentIntensity } = this.state;

        const leftIntensity = currentIntensity * 0.7;
        const bottomIntensity = currentIntensity * 0.6;

        // Apply vignette box-shadow effect with directional emphasis
        const vignetteBoxShadow = `
        inset 0 0 ${currentVignetteSize}px ${currentVignetteBlur}px rgba(255, 255, 255, ${0.4 + currentIntensity * 0.6}),
        inset 0 0 ${currentVignetteSize * 0.7}px ${currentVignetteBlur * 0.7}px rgba(255, 255, 255, ${currentIntensity * 0.8}),
        inset 0 0 ${currentVignetteSize * 0.5}px ${currentVignetteBlur * 0.5}px rgba(59, 130, 246, ${currentIntensity * 0.6}),
        inset ${currentVignetteSize * 0.4}px 0 ${currentVignetteBlur * 1.2}px ${currentVignetteBlur * 0.3}px rgba(255, 255, 255, ${leftIntensity}),
        inset 0 ${currentVignetteSize * 0.3}px ${currentVignetteBlur * 1.2}px ${currentVignetteBlur * 0.3}px rgba(59, 130, 246, ${bottomIntensity})
    `;
        this.elements.vignette.style.boxShadow = vignetteBoxShadow;

        // Apply frequency-based color gradient with directional bias
        const hue = 200 + frequency / 10;
        const glowBackground = `
        radial-gradient(ellipse 140% 120% at 35% 70%,
            hsla(${hue}, 80%, 60%, ${currentIntensity * 0.35}) 0%,
            hsla(${hue + 30}, 70%, 50%, ${currentIntensity * 0.18}) 30%,
            transparent 70%
        )
    `;
        this.elements.glowLayer.style.background = glowBackground;

        // Apply pulse ring scaling
        const ringSize = 200 + currentIntensity * 600;
        this.elements.pulseRing.style.width = `${ringSize}px`;
        this.elements.pulseRing.style.height = `${ringSize}px`;
    }


    public disconnect(): void {
        if (this.reconnectTimeout) {
            clearTimeout(this.reconnectTimeout);
            this.reconnectTimeout = null;
        }
        if (this.ws) {
            this.ws.close(1000, 'Client disconnect');
            this.ws = null;
        }
    }

    public reconnect(): void {
        this.disconnect();
        this.reconnectAttempts = 0;
        this.connect();
    }

    public isConnected(): boolean {
        return this.ws?.readyState === WebSocket.OPEN;
    }

    public setLerpFactor(factor: number): void {
        this.options.lerpFactor = Math.max(0, Math.min(1, factor));
    }

    public destroy(): void {
        this.disconnect();
        if (this.overlayWrapper?.parentNode) {
            this.overlayWrapper.parentNode.removeChild(this.overlayWrapper);
        }
    }
}

export function createAudioVisualiser(
    container: string | HTMLElement,
    options?: AudioVisualiserOptions
): AudioVisualiserAPI {
    return new AudioVisualiser(container, options);
}

export default createAudioVisualiser;
