"""
Puente para transmitir el estado de la flota de AGVs, tick a tick, desde la
simulacion en Python (agentpy) hacia Unity, usando un socket TCP sencillo.

Python actua como servidor porque ya es dueno del bucle de simulacion.
Unity se conecta una sola vez como cliente al arrancar la escena y recibe,
por cada tick, una linea de texto en formato JSON terminada en salto de
linea.

Forma del mensaje que se envia:

{
  "t": 137,
  "pending": 3,
  "agvs": [
    {"id": 1, "x": 12, "y": 5, "status": "Transporting", "battery": 72.4,
     "completed": 4, "carrying": "PLB-01-1", "destination": "R-11-E2"},
    ...
  ],
  "peds": [{"x": 20, "y": 8}, ...],
  "closed": [{"x": 14, "y": 9}, ...],
  "pallets": [{"id": "PLB-01-1", "status": "reserved"}, ...]
}

"carrying" es el id del pallet de origen de la mision activa mientras el
AGV va en camino a recogerlo (status ToPickup) o ya lo trae encima (status
Transporting); en cualquier otro momento va en null. "destination" es el id
del pallet destino, solo mientras status es Transporting; sirve para que
Unity sepa donde reaparecer la carga al completarse la mision.

"pallets" solo incluye los que NO estan en su estado normal ("stored"),
para no mandar los ~75 pallets del layout completo en cada tick. Los
estados que puede traer son: reserved (un AGV va en camino a recogerlo),
being transported (ya esta sobre el AGV, debe ocultarse en su posicion de
origen), delivered (la mision ya se completo), y waiting for pickup (una
mision se cayo por bateria y el pallet volvio a estar disponible en su
origen, aunque tecnicamente sigue "pendiente" en vez de "stored").

Uso dentro del notebook (solo para la demo en vivo contra Unity, no debe
usarse dentro de train()/evaluate(), que deben seguir corriendo tal cual):

    from python_unity_bridge import UnityBridge

    bridge = UnityBridge(port=5005)
    bridge.start()  # se queda esperando a que Unity se conecte

    sc = Scenario(seed=SHOW_SEED, horizon=400)
    model = DistributionCenter(dict(
        scenario=sc, strategy="qlearn", elements=elements, pallets=pallets,
        walkable=walkable, oracle=oracle, qtables=qtables, learning=False,
        alpha=0.0, epsilon=0.0, policy_seed=SHOW_SEED, keep_history=True))
    model.setup()

    for _ in range(sc.horizon):
        model.step()
        model.update()
        bridge.send_tick(model)
        # opcional: frenar al ritmo real para que la interpolacion en Unity
        # se vea natural, por ejemplo time.sleep(0.15)

    bridge.close()
"""

import json
import socket
import threading


class UnityBridge:
    def __init__(self, host="127.0.0.1", port=5005):
        self.host = host
        self.port = port
        self._server = None
        self._conn = None
        self._lock = threading.Lock()

    def start(self, wait_for_client=True):
        """Abre el socket servidor. Si wait_for_client es True, bloquea
        hasta que Unity se conecte, que es lo mas simple para una demo."""
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind((self.host, self.port))
        self._server.listen(1)
        print(f"[UnityBridge] esperando a Unity en {self.host}:{self.port} ...")
        if wait_for_client:
            self._accept()
        else:
            threading.Thread(target=self._accept, daemon=True).start()

    def _accept(self):
        conn, addr = self._server.accept()
        with self._lock:
            self._conn = conn
        print(f"[UnityBridge] Unity conectado desde {addr}")

    def is_connected(self):
        with self._lock:
            return self._conn is not None

    def _mission_of(self, model, mission_id):
        if mission_id is None:
            return None
        return next((m for m in model.mm.missions if m["id"] == mission_id), None)

    def _agv_entry(self, model, a):
        mission = self._mission_of(model, a.mission)
        carrying = None
        destination = None
        if mission is not None:
            if a.status in ("ToPickup", "Transporting"):
                carrying = mission["origin"]
            if a.status == "Transporting":
                destination = mission["destination"]
        return {
            "id": a.id,
            "x": int(a.x),
            "y": int(a.y),
            "status": a.status,
            "battery": round(float(a.battery), 1),
            "completed": int(a.completed),
            "carrying": carrying,
            "destination": destination,
        }

    def _snapshot(self, model):
        return {
            "t": model.t,
            "pending": len(model.mm.pending()),
            "agvs": [self._agv_entry(model, a) for a in model.agvs],
            "peds": [
                {"x": int(p[0]), "y": int(p[1])}
                for p in getattr(model, "pedestrians", [])
            ],
            "closed": [
                {"x": int(c[0]), "y": int(c[1])}
                for c in getattr(model, "dyn_blocked", [])
            ],
            "pallets": [
                {"id": pid, "status": p["status"]}
                for pid, p in model.pallet_by_id.items()
                if p["status"] != "stored"
            ],
        }

    def send_tick(self, model):
        """Serializa el estado actual del modelo y lo envia a Unity.
        Si Unity todavia no se ha conectado, no hace nada."""
        with self._lock:
            conn = self._conn
        if conn is None:
            return
        payload = (json.dumps(self._snapshot(model)) + "\n").encode("utf-8")
        try:
            conn.sendall(payload)
        except OSError:
            print("[UnityBridge] Unity se desconecto")
            with self._lock:
                self._conn = None

    def close(self):
        with self._lock:
            conn, self._conn = self._conn, None
        if conn:
            try:
                conn.close()
            except OSError:
                pass
        if self._server:
            try:
                self._server.close()
            except OSError:
                pass
