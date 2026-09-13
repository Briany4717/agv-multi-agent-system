// AGVUnityBridge.cs
//
// Cliente TCP que se conecta al servidor abierto por python_unity_bridge.py,
// recibe una linea de JSON por tick con el estado de la flota de AGVs, los
// peatones, los cierres temporales de pasillo y el estado de los pallets
// que estan en movimiento, y mueve/actualiza los prefabs del paquete Unity
// Warehouse (Palletrobot, Worker, y los pallets del layout estatico).
//
// Colocar este componente en un GameObject vacio de la escena
// WarehouseSceneSample y asignar en el inspector:
//   - agvPrefab             -> Scene_Warehouse/Movable/Prefabs/Palletrobot
//   - pedestrianPrefab      -> Scene_Warehouse/Movable/Prefabs/Worker
//   - carriedPalletPrefab   -> el mismo prefab de pallet que uses en el
//                              LayoutImporter (Pallet, Cardboard_A1, etc.),
//                              se usa como carga visible sobre el AGV
//                              mientras transporta algo.
//   - gridOrigin            -> la esquina minima de Building_Collider.bounds.
//                              Con la medicion real de este proyecto
//                              (5.93, 0.00, 2.41) ya viene precargada abajo.
//   - cellSize              -> 1 (una celda del notebook equivale a un metro)
//   - swapXZAxes            -> true en este proyecto (ver comentario en
//                              GridToWorld mas abajo).
//
// Como encuentra los pallets del layout: al arrancar, busca en la escena
// un objeto llamado "AGV_Layout" (el mismo que crea LayoutImporter.cs) y
// junta todos sus hijos cuyo nombre empiece con "pallet_", indexandolos por
// el id que sigue a ese prefijo (por ejemplo "pallet_PLB-01-1" -> id
// "PLB-01-1"). Por eso es importante importar el layout ANTES de correr la
// escena con este componente activo.
//
// Este puente es de una sola via: Python manda, Unity solo aplica. No hay
// logica de colision ni de negociacion aqui, eso ya vive en Python.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[Serializable]
public class AGVState
{
    public int id;
    public int x;
    public int y;
    public string status;
    public float battery;
    public int completed;
    public string carrying;     // id del pallet de origen, o null si no trae nada
    public string destination;  // id del pallet destino, solo mientras Transporting
}

[Serializable]
public class CellPos
{
    public int x;
    public int y;
}

[Serializable]
public class PalletState
{
    public string id;
    public string status; // "reserved", "being transported", "delivered", "waiting for pickup"
}

[Serializable]
public class TickSnapshot
{
    public int t;
    public int pending;
    public AGVState[] agvs;
    public CellPos[] peds;
    public CellPos[] closed;
    public PalletState[] pallets;
}

public class AGVUnityBridge : MonoBehaviour
{
    [Header("Conexion")]
    public string host = "127.0.0.1";
    public int port = 5005;

    [Header("Prefabs (Unity Warehouse)")]
    public Transform agvPrefab;           // Movable/Prefabs/Palletrobot
    public Transform pedestrianPrefab;    // Movable/Prefabs/Worker
    public Transform carriedPalletPrefab; // el prefab de pallet a mostrar sobre el AGV

    [Header("Mapeo de rejilla")]
    // Esquina minima medida de Building_Collider.bounds en este proyecto.
    // Vuelve a correr BoundsMeasurer.cs si el edificio o la escena cambian.
    public Vector3 gridOrigin = new Vector3(5.929f, 0.000f, 2.406f);
    public float cellSize = 1f;               // 1 celda del notebook = 1 metro

    // El edificio real es mas profundo (Z = 64.68 m) que ancho (X = 37.49 m),
    // al reves del layout por defecto del notebook. Con esto en true, el
    // eje x del grid (el eje largo del layout, P width) se manda al eje Z
    // de Unity en vez de al eje X.
    public bool swapXZAxes = true;

    [Header("Movimiento")]
    public float moveSpeedCellsPerSecond = 2f;

    [Header("Carga sobre el AGV")]
    public Vector3 carriedPalletLocalOffset = new Vector3(0f, 0.6f, 0f);

    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _readThread;
    private volatile bool _running;

    // solo se conserva la ultima linea recibida, no hace falta procesar
    // instantaneas viejas si Python va mas rapido que el frame rate
    private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();

    private readonly Dictionary<int, Transform> _agvInstances = new Dictionary<int, Transform>();
    private readonly Dictionary<int, Vector3> _agvTargets = new Dictionary<int, Vector3>();
    private readonly List<Transform> _pedInstances = new List<Transform>();

    // pallets ya colocados en la escena por LayoutImporter, indexados por
    // el id que traen despues de "pallet_" en su nombre.
    private readonly Dictionary<string, GameObject> _palletObjects = new Dictionary<string, GameObject>();

    // una copia de pallet "cargado" por cada AGV que esta transportando algo
    private readonly Dictionary<int, GameObject> _carriedPallets = new Dictionary<int, GameObject>();

    private void Start()
    {
        IndexExistingPallets();
        Connect();
    }

    // Recorre "AGV_Layout" (creado por LayoutImporter.cs) y junta todos los
    // hijos que se llamen "pallet_<id>", para poder esconderlos y volverlos
    // a mostrar segun lo que reporte Python.
    private void IndexExistingPallets()
    {
        GameObject layoutRoot = GameObject.Find("AGV_Layout");
        if (layoutRoot == null)
        {
            Debug.LogWarning("[AGVUnityBridge] No se encontro 'AGV_Layout' en la escena. " +
                              "Importa el layout con LayoutImporter antes de conectar el puente " +
                              "si quieres que los pallets se oculten/muestren.");
            return;
        }

        foreach (Transform child in layoutRoot.transform)
        {
            if (!child.name.StartsWith("pallet_")) continue;
            string id = child.name.Substring("pallet_".Length);
            _palletObjects[id] = child.gameObject;
        }
        Debug.Log($"[AGVUnityBridge] {_palletObjects.Count} pallets del layout indexados.");
    }

    private void Connect()
    {
        try
        {
            _client = new TcpClient();
            _client.Connect(host, port);
            _stream = _client.GetStream();
            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
            Debug.Log($"[AGVUnityBridge] conectado a {host}:{port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AGVUnityBridge] no se pudo conectar a {host}:{port} - {e.Message}");
        }
    }

    // Corre en un hilo aparte: lee del socket y arma lineas completas
    // separadas por salto de linea, tal como las escribe UnityBridge.send_tick
    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        try
        {
            while (_running)
            {
                int n = _stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                builder.Append(Encoding.UTF8.GetString(buffer, 0, n));

                int newlineIdx;
                while ((newlineIdx = builder.ToString().IndexOf('\n')) >= 0)
                {
                    string line = builder.ToString(0, newlineIdx);
                    builder.Remove(0, newlineIdx + 1);
                    if (!string.IsNullOrWhiteSpace(line))
                        _incoming.Enqueue(line);
                }
            }
        }
        catch (Exception e)
        {
            if (_running)
                Debug.LogWarning($"[AGVUnityBridge] conexion cerrada: {e.Message}");
        }
    }

    private void Update()
    {
        // se queda solo con la instantanea mas reciente encolada
        string latest = null;
        while (_incoming.TryDequeue(out var line))
            latest = line;

        if (latest != null)
            ApplySnapshot(latest);

        // mueve todos los AGVs instanciados hacia su celda objetivo
        foreach (var kv in _agvInstances)
        {
            if (!_agvTargets.TryGetValue(kv.Key, out var target)) continue;
            var t = kv.Value;
            Vector3 prevPos = t.position;
            t.position = Vector3.MoveTowards(
                t.position, target, moveSpeedCellsPerSecond * cellSize * Time.deltaTime);

            Vector3 delta = t.position - prevPos;
            if (delta.sqrMagnitude > 0.0001f)
                t.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        }
    }

    private void ApplySnapshot(string json)
    {
        TickSnapshot snap;
        try
        {
            snap = JsonUtility.FromJson<TickSnapshot>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AGVUnityBridge] JSON invalido: {e.Message}");
            return;
        }
        if (snap == null) return;

        if (snap.agvs != null)
        {
            foreach (var a in snap.agvs)
            {
                if (!_agvInstances.TryGetValue(a.id, out var t))
                {
                    if (agvPrefab == null) continue;
                    t = Instantiate(agvPrefab);
                    t.name = $"AGV-{a.id}";
                    _agvInstances[a.id] = t;
                    t.position = GridToWorld(a.x, a.y); // primera vez: sin interpolar
                }
                _agvTargets[a.id] = GridToWorld(a.x, a.y);
                UpdateCarriedPallet(a);
                // aqui es donde se puede leer a.status y a.battery para,
                // por ejemplo, cambiar el color de un material segun el
                // STATUS_COLOR que ya usa el notebook para graficar
            }
        }

        if (snap.peds != null)
        {
            for (int i = 0; i < snap.peds.Length; i++)
            {
                if (i >= _pedInstances.Count)
                {
                    if (pedestrianPrefab == null) continue;
                    var t = Instantiate(pedestrianPrefab);
                    t.name = $"Pedestrian-{i}";
                    _pedInstances.Add(t);
                }
                _pedInstances[i].position = GridToWorld(snap.peds[i].x, snap.peds[i].y);
            }
        }

        if (snap.pallets != null)
            ApplyPalletStates(snap.pallets);
    }

    // Muestra u oculta cada pallet del layout estatico segun el estado que
    // reporta Python. "stored" nunca aparece aqui (python_unity_bridge.py
    // lo omite porque es el estado normal), asi que cualquier pallet que
    // no venga en la lista se asume "stored" y se deja visible.
    private void ApplyPalletStates(PalletState[] pallets)
    {
        foreach (var p in pallets)
        {
            if (!_palletObjects.TryGetValue(p.id, out var go)) continue;

            bool visibleAtOrigin = p.status == "waiting for pickup";
            go.SetActive(visibleAtOrigin);
        }
    }

    // Mientras un AGV va "ToPickup" o "Transporting", le pega una copia del
    // pallet como carga visible encima. En cuanto deja de traer nada
    // (carrying llega en null/vacio), destruye esa copia.
    // Solo se ve la carga sobre el AGV cuando ya la trae encima de verdad
    // (status Transporting). Mientras el status es ToPickup, el AGV va en
    // camino a recogerla pero todavia no la tiene, aunque "carrying" ya
    // este asignado (eso sirve para esconder el pallet de origen apenas
    // se reserva, no para dibujar la carga antes de tiempo).
    private void UpdateCarriedPallet(AGVState a)
    {
        bool shouldCarry = a.status == "Transporting"
            && !string.IsNullOrEmpty(a.carrying)
            && carriedPalletPrefab != null;

        if (shouldCarry)
        {
            if (!_carriedPallets.ContainsKey(a.id))
            {
                GameObject carried = Instantiate(carriedPalletPrefab.gameObject, _agvInstances[a.id]);
                carried.name = $"Carga_AGV-{a.id}";
                carried.transform.localPosition = carriedPalletLocalOffset;
                carried.transform.localRotation = Quaternion.identity;
                _carriedPallets[a.id] = carried;
            }
        }
        else if (_carriedPallets.TryGetValue(a.id, out var existing))
        {
            Destroy(existing);
            _carriedPallets.Remove(a.id);
        }
    }

    // celda (x, y) del notebook -> posicion de mundo en Unity.
    // Sin rotar: grid x -> Unity X, grid y -> Unity Z.
    // Con swapXZAxes: grid x -> Unity Z, grid y -> Unity X, para que el eje
    // mas largo del layout (P width, el eje x del grid) caiga sobre el eje
    // mas largo real del edificio (Z, 64.68 m en este proyecto) en vez de
    // aplastarse contra el eje mas corto (X, 37.49 m).
    private Vector3 GridToWorld(int x, int y)
    {
        float alongGridX = (x + 0.5f) * cellSize;
        float alongGridY = (y + 0.5f) * cellSize;
        float worldX = swapXZAxes ? alongGridY : alongGridX;
        float worldZ = swapXZAxes ? alongGridX : alongGridY;
        return gridOrigin + new Vector3(worldX, 0f, worldZ);
    }

    private void OnDestroy()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _readThread?.Join(200);
    }
}