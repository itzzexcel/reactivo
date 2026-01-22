/*
Le caes a todos bien
Todos te ríen la gracia
Tienes un gesto extraño
A mí no me engañas

Así que eres humilde
Teniendo un barco en casa
Arqueaste la ceja
Porque por mí no pasas

No me mola tu forma de hablar
No me fío
No me importa dónde quieres llegar
No me fío

Estás usando tu control mental
Quieres hacerme el lío
No me trago tu juego emocional
No me fío

No creías tan pronto
Verte así destapado
Conmigo no sirve eso
De hacerte el colocado

Tranquilo, no diré nada
Me la sudan tus juegos
Solo mantén la distancia
Y no me rompas los huevo

No me mola tu forma de hablar
No me fío
No me importa dónde quieres llegar
No me fío

Estás usando tu control mental
Quieres hacerme el lío
No me trago tu juego emocional
No me fío
*/

import { LunaUnload, Tracer } from "@luna/core";
import { MediaItem, redux } from "@luna/lib";
import { GetNPView } from "./ui-interface";
import createAudioVisualiser, { AudioVisualiserAPI } from "./giragira";

export const { trace, errSignal } = Tracer("[reactivo]");
export const unloads = new Set<LunaUnload>();

// Visualiser instance (initially null)
let visualiser: AudioVisualiserAPI | null = null;
export let availableDevices: object | null = null;
export let currentDevice : string = "";

/**
 * Initialises or reinitialises the visualiser
 */
const initVisualiser = (): void => {
	try {
		// Clean up previous instance if it exists
		if (visualiser) {
			visualiser.disconnect();
			visualiser.destroy();
			visualiser = null;
		}

		// Get container element
		const nowPlaying = GetNPView();

		// Create new visualiser instance
		visualiser = createAudioVisualiser(nowPlaying, {
			lerpFactor: 0.5,
			wsUrl: 'ws://localhost:5343',
			autoReconnect: true,
			maxReconnectAttempts: 100,
			showStatus: false,
			showStats: false,	
		});

	} catch (error) {
		console.error("Failed to initialize visualiser:", error);
		visualiser = null;
	}

	// TODO: Get the current playing device and search it in the bozo zozo levle awwrrayy 
	
};

/**
 * Ensures visualiser is connected, reconnects if necessary
 */
const ensureVisualiserConnected = (): void => {
	if (!visualiser) {
		initVisualiser();
		return;
	}

	if (!visualiser.isConnected()) {
		visualiser.reconnect();
	}
};

/**
 * Initialises visualiser when DOM is ready
 */
const initWhenReady = (): void => {
	if (document.readyState === 'loading') {
		document.addEventListener('DOMContentLoaded', initVisualiser);
	} else {
		initVisualiser();
	}
};

// Start initialisation
initWhenReady();

redux.intercept("view/ENTERED_NOWPLAYING", unloads, function () {
	visualiser?.togglePause(true);
	if (!visualiser) {
		initVisualiser();
	} else {
		visualiser.reconnect();
		if (visualiser.isConnected()) {
			console.log("reconnected successfully");
		}
	}
});

redux.intercept("view/EXITED_NOWPLAYING", unloads, function () {
	visualiser?.togglePause(false);	
	visualiser?.disconnect();
	console.log("visualiser disconnected");

});

redux.intercept("player/SET_ACTIVE_DEVICE_SUCCESS", unloads, function (x: any) {
	
	if (Array.isArray(availableDevices) && availableDevices.length === 0) {
		console.log("No available devices, forcing the redux to set again the thingaling bleh");
		currentDevice = redux.store["player/SET_AVAILABLE_DEVICES"]([]);
	}
	
	if (Array.isArray(availableDevices)) {
		let deviceObject = availableDevices.find((d: any) => d.id === x);
    if (deviceObject) {
        console.log("Native Device ID:", deviceObject.nativeDeviceId);
		currentDevice = deviceObject.nativeDeviceId;
		if (visualiser) {
			visualiser.deviceChanged(deviceObject.nativeDeviceId);
		}
	}}
});


redux.intercept("player/SET_AVAILABLE_DEVICES", unloads, function (x: any) {
	availableDevices = x;
	console.log("Devices updated:", availableDevices);
});

unloads.add(() => {
	if (visualiser) {
		try {
			visualiser.disconnect();
			visualiser.destroy();
		} catch (error) {
			console.error("Error destroying visualiser:", error);
		} finally {
			visualiser = null;
		}
	}
});

MediaItem.onMediaTransition(unloads, async (mediaItem) => {
	if (!mediaItem) {
		if (visualiser) {
			visualiser.disconnect();
		}
		return;
	}

	try {
		if (!visualiser) {
			initVisualiser();
			return;
		}

		ensureVisualiserConnected();

		visualiser.setLerpFactor(0.5);

	} catch (error) {
		console.error("Error in media transition:", error);
		initVisualiser();
	}
});

export { initVisualiser, ensureVisualiserConnected };
