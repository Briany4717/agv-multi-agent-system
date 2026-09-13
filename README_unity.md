# Unity Visual Simulation — AGV Digital Twin

This document explains what the Unity part of the project contains and how to install and run it together with the Python multi-agent system. It is meant to go inside the GitHub repository required by the assignment, in the section that asks for installation and execution instructions, and to serve as a reference when writing the computational and visual integration section of the technical report.

## What this scene represents

The scene starts from the free Unity Warehouse package by Unity Technologies Japan, and was adapted to the real dimensions of the warehouse included in that package instead of forcing it onto an arbitrary grid. On top of that base, its prefabs are reused to represent every element of the environment defined in the notebook:

The AGVs in the fleet are represented with the Palletrobot prefab, which is already a cabless autonomous floor robot, so it was not replaced by anything else. The pedestrians the system simulates are represented with the Worker prefab. The storage racks were built by repeating the Shelf prefab until each block was covered. Pallet positions, whether empty or loaded, use the Pallet, PlasticBox and Cardboard prefabs, plus Sunoko as the base for a free position. The building and its perimeter walls are the Building prefab together with its collider.

The production line, the truck dock and the charging stations have no dedicated prefab in the original package, so they were assembled by combining Pedestal, Trolley and floor markers, as described in the project's earlier analysis.

## What the bridge with Python does

The complete multi-agent system — that is, mission assignment, negotiation among AGVs, routing, and the decision of when to charge battery — lives entirely in the Python notebook with agentpy. Unity does not decide anything, it only receives and represents. Synchronization works as follows: Python runs as a TCP server and, on every simulation tick, sends a line of text in JSON format with the position, status and battery of each AGV, plus the position of the pedestrians and the temporarily closed cells. The AGVUnityBridge.cs component, placed in the scene, connects as a client to that server, instantiates a Palletrobot the first time a new AGV appears and a Worker for each pedestrian, and moves those objects toward the received cell by interpolating the movement between one message and the next.

This means the startup order matters: Python must be listening before Unity attempts to connect.

It is worth clarifying the current state of that integration for whoever reads the report: the message that reaches Unity already carries the status and battery of each AGV, but in the current version of the component that data is not yet drawn on screen, it is only used to move the AGV. Displaying the ID, status, battery and mission of each AGV on top of the 3D model, which the challenge asks for whenever feasible, remains the natural next step on top of this same foundation, for example by adding a floating text label or changing the robot's color according to its status, reusing the STATUS_COLOR palette the notebook already uses for its own charts.

## Requirements

Unity 2022.3.16 or a more recent compatible version, with the HDRP render pipeline, which is what the Unity Warehouse package is built with. Python 3.9 or higher to run the notebook and the bridge script. No additional Unity package needs to be installed, because AGVUnityBridge.cs only uses TcpClient and JsonUtility, which are already included in Unity.

## Installation

Import this repository's .unitypackage into the Unity project, or clone the complete project if the repository includes it that way. Open the environment's main scene. Verify that an object with the AGVUnityBridge component exists in the scene and check its fields in the inspector: agvPrefab must point to the Palletrobot prefab, pedestrianPrefab to the Worker prefab, gridOrigin must hold the world position corresponding to cell (0, 0) of the notebook, that is, the building's minimum corner, and cellSize must be set to 1, because one cell in the notebook equals one meter in the scene.

On the Python side, place the python_unity_bridge.py file next to the notebook, or in a folder the notebook can import from.

## Execution

First run the Python block that creates the bridge and calls its start method, which leaves the server waiting for Unity's connection. Then press Play on the Unity scene, which in its Start method attempts to connect to that server. Once connected, run the simulation loop in Python by calling send_tick after every step of the model, and in Unity the Palletrobots should start appearing at their initial positions and moving as the simulation progresses, together with the Workers representing the pedestrians.

If you want to validate the coordinate mapping and the prefabs without depending on the live connection, it is best to first record a run with keep_history enabled in the notebook and replay that history in Unity from the exported JSON file, before testing live mode with the socket.

## For the report's integration section

The explanation in this document, especially the part about what information travels in each message and how Python's ticks are synchronized with movement in Unity, is the basis for answering the question there of how the multi-agent system and the visual environment stay synchronized. It is worth backing it up with Unity screenshots showing the full warehouse, the AGVs in different states, and, if already implemented, the labels or colors reflecting each AGV's status and battery.