﻿using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.UI;
using Random = Unity.Mathematics.Random;

public class ParticleSimManager : MonoBehaviour {
    //public ComputeShader particleSimShader;
    public Material particleDebugMaterial;
    public Mesh particleMesh;

    public uint particleCount = 1000;

    public float boundaryDistance = 1f;
    //public float cellSize = 0.05f;

    public float  particleMass;
    public float  wallDamping;
    public float  particleStiffness;
    public float  particleRestingDensity;
    public float  particleViscosity;
    [Range(0f, 0.5f)] public float  smoothingLength;
    public float  timeStep;
    public float4 gravity;

    private ComputeBuffer drawArgs;
    private uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

    private ComputeBuffer particleBuffer;
    //private ComputeBuffer particleBufferA;
    //private ComputeBuffer particleBufferB;
    //private bool pingPong;
    
    //private int          verletIndex;
    //private int densityPressureIndex;
    //private int           forceIndex;
    
    struct Particle {
        public float3 position;
        public float  density;
        public float3 velocity;
        public float  pressure;
        public float3 force;
        public uint   type; // 0 = earth, 1 = water, 2 = fire, 3 = wood, 4 = metal
        public float  temperature;
        public float  remainingLifetime;
        public float  pad0;
        public float  pad1;
    }

    //private NativeList<Particle> waterList;
    //private NativeList<Particle>  woodList;
    //private NativeList<Particle>  fireList;
    private NativeList<Particle> particleList;
    //private NativeList<Matrix4x4> matricesList;
    private NativeMultiHashMap<int, int> particleSpatialHashMap;

    private JobHandle updateSpatialHashMapHandle;
    private JobHandle updateParticlesHandle;
    //private JobHandle updateMatricesHandle;

    public static ParticleSimManager instance;
    
    private void Awake() {
        instance = this;
        
        /*
        particleBufferA = new ComputeBuffer(256 * 256 * 256, sizeof(float) * 12);
        particleBufferB = new ComputeBuffer(256 * 256 * 256, sizeof(float) * 12);
        
                 verletIndex = particleSimShader.FindKernel("Verlet");
        densityPressureIndex = particleSimShader.FindKernel("DensityPressure");
                  forceIndex = particleSimShader.FindKernel("Force");
        */
        
        particleBuffer = new ComputeBuffer(256 * 256 * 256, sizeof(float) * 16);
        
        drawArgs = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        args[0] = particleMesh.GetIndexCount(0);
        args[2] = particleMesh.GetIndexStart(0);
        args[3] = particleMesh.GetBaseVertex(0);

        particleList = new NativeList<Particle>(Allocator.Persistent);
        
        InitializeParticles();
    }

    private void OnGUI() {
        if (GUI.Button(new Rect(10f, 10f, 200f, 20f), "Reinit")) 
            InitializeParticles();
    }

    private void InitializeParticles() {
        var initialParticles = new Particle[particleCount];
        
        var rand = new Random((uint)Mathf.RoundToInt(Time.time * 100000f + 1f));
        for (int i = 0; i < particleCount; i++) {
            initialParticles[i] = new Particle();
            initialParticles[i].position = rand.NextFloat3(new float3(-1f, -1f, -1f) * 0.5f, new float3(1f, 1f, 1f) * 0.5f);
            initialParticles[i].velocity = rand.NextFloat3(new float3(-1f, -1f, -1f), new float3(1f, 1f, 1f));
            initialParticles[i].density = particleRestingDensity;
            initialParticles[i].pressure = 1f;
            //Debug.Log($"{initialParticles[i].position} {initialParticles[i].velocity}");
            particleList.Add(initialParticles[i]);
        }
        
        //particleList.add
        //particleList.CopyFrom(initialParticles);
        //particleBufferA.SetData(initialParticles);
    }

    [BurstCompile]
    private struct PopulateSpatialHashMapJob : IJobParallelFor {
        [ReadOnly] public float cellSize;
        [ReadOnly] public float boundsSize;
        
        [ReadOnly] public NativeArray<Particle> particles;
        //[WriteOnly] public NativeMultiHashMap<int, int> particleSpatialHashMap;
        [WriteOnly] public NativeMultiHashMap<int, int>.ParallelWriter particleSpatialHashMapWriter;
        
        public void Execute(int i) {
            //var parallelHashMap = particleSpatialHashMap.AsParallelWriter();
            int hash = (int)math.hash(new int3(math.floor((particles[i].position - boundsSize) / cellSize)));
            particleSpatialHashMapWriter.Add(hash, i);
        }
    }

    [BurstCompile]
    private struct UpdateParticlesJob : IJobParallelFor {
        [ReadOnly] public float deltaTime;
        [ReadOnly] public float cellSize;
        [ReadOnly] public float boundsSize;
        
        [ReadOnly] public NativeMultiHashMap<int, int> particleSpatialHashMap;
        public NativeArray<Particle> particles;
        
        public void Execute(int i) {
            int3 thisBucket = new int3(math.floor((particles[i].position - boundsSize) / cellSize));
            
            // check all buckets adjacent to this one
            /*
            for (int x = -1; x <= 1; x++) {
                for (int y = -1; y <= 1; y++) {
                    for (int z = -1; z <= 1; z++) {
                        int hash = (int)math.hash(thisBucket + new int3(x,y,z));
                        if (particleSpatialHashMap.ContainsKey(hash)) {
                            foreach (int otherParticleIndex in particleSpatialHashMap.GetValuesForKey(hash)) {
                                //Particle otherParticle = particles[otherParticleIndex];
                            }
                        }
                    }
                }
            }
            */
            
            // update this particle
            Particle thisParticle = particles[i];
            float3 acceleration = thisParticle.force / thisParticle.density;
            float3 velocity = thisParticle.velocity + deltaTime * acceleration;// + timeStep * gravity;
    
            if (thisParticle.type == 2)
                velocity = new float3(0,0,0);
    
            float3 position = thisParticle.position + deltaTime * velocity; 
            //velocity *= 0.985;

            // TODO: apply sphere constraint
            if (math.length(position) > 1.0f) {
                position = math.normalize(position) * 1.0f;
                //velocity -= normalize(position) * particleRadius;
                velocity *= -0.3f;
            }
    
            thisParticle.velocity = velocity;
            thisParticle.position = position;
            particles[i] = thisParticle;
        }
    }

    /*
    [BurstCompile]
    private struct PopulateParticleMatricesJob : IJobParallelFor {
        [ReadOnly] public NativeList<Particle> particles;
        
        [WriteOnly] public NativeList<Matrix4x4> transformMatrices;

        public void Execute(int index) {
            throw new NotImplementedException();
        }
    }
    */
    
    private void Update() {
        //int cellCount = Mathf.CeilToInt((boundaryDistance * 2f) / smoothingLength);
        particleSpatialHashMap = new NativeMultiHashMap<int, int>(particleList.Length, Allocator.TempJob);
        //matricesList = new NativeList<Matrix4x4>(Allocator.TempJob);
        
        var populateSpatialHashMapJob = new PopulateSpatialHashMapJob();
        populateSpatialHashMapJob.particleSpatialHashMapWriter = particleSpatialHashMap.AsParallelWriter();
        populateSpatialHashMapJob.boundsSize = boundaryDistance;
        populateSpatialHashMapJob.cellSize = smoothingLength;
        populateSpatialHashMapJob.particles = particleList;

        updateSpatialHashMapHandle = populateSpatialHashMapJob.Schedule(particleList.Length, 64);
        
        var updateParticlesJob = new UpdateParticlesJob();
        updateParticlesJob.particleSpatialHashMap = particleSpatialHashMap;
        updateParticlesJob.boundsSize = boundaryDistance;
        updateParticlesJob.cellSize = smoothingLength;
        updateParticlesJob.particles = particleList;
        updateParticlesJob.deltaTime = 1f / 72f;

        updateParticlesHandle = updateParticlesJob.Schedule(particleList.Length, 16, updateSpatialHashMapHandle);

        /*
        var updateMatricesJob = new PopulateParticleMatricesJob();
        updateMatricesJob.particles = particleList;
        updateMatricesJob.transformMatrices = matricesList;

        updateMatricesHandle = updateMatricesJob.Schedule(particleList.Length, 64);
        */

        /*
        //Debug.Log($"{particleCount}");
        particleSimShader.SetInt("particleCount", (int)particleCount);
        
        //PingPongBuffers();
        particleSimShader.SetBuffer(verletIndex, "particles", particleBufferA);
        particleSimShader.SetBuffer(densityPressureIndex, "particles", particleBufferA);
        particleSimShader.SetBuffer(forceIndex, "particles", particleBufferA);
        
        particleSimShader.SetFloat("pi", Mathf.PI);
        particleSimShader.SetFloat("particleMass", particleMass);
        particleSimShader.SetFloat("wallDamping", wallDamping);
        particleSimShader.SetFloat("particleStiffness", particleStiffness);
        particleSimShader.SetFloat("particleRestingDensity", particleRestingDensity);
        particleSimShader.SetFloat("particleViscosity", particleViscosity);
        particleSimShader.SetFloat("smoothingLength", smoothingLength);
        particleSimShader.SetFloat("timeStep", timeStep);
        particleSimShader.SetVector("gravity", gravity);
        
        particleSimShader.Dispatch(densityPressureIndex, Mathf.CeilToInt(particleCount / 128f), 1, 1);
        particleSimShader.Dispatch(forceIndex,           Mathf.CeilToInt(particleCount / 128f), 1, 1);
        particleSimShader.Dispatch(verletIndex,          Mathf.CeilToInt(particleCount / 128f), 1, 1);
        */
    }

    private void OnDestroy() {
        particleList.Dispose();
        //particleBufferA.Release();
        //particleBufferB.Release();
    }

    private void LateUpdate() {
        updateParticlesHandle.Complete();
        particleSpatialHashMap.Dispose();

        args[1] = particleCount;
        drawArgs.SetData(args);
        
        particleBuffer.SetData(particleList.AsArray());
        
        particleDebugMaterial.SetPass(0);
        //particleDebugMaterial.SetBuffer("_ParticleBuffer", pingPong ? particleBufferA : particleBufferB);
        particleDebugMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        
        Graphics.DrawMeshInstancedIndirect(particleMesh, 0, particleDebugMaterial, new Bounds(Vector3.zero, Vector3.one * 1000f), drawArgs);
    }

    public void SpawnParticle(float3 position, float3 velocity) {
        Particle newParticle = new Particle {position = position, velocity = velocity};
        particleList.Add(newParticle);
        //particleBufferA.SetData(new[] { newParticle }, 0, (int)particleCount, 1);
        particleCount++;
    }
}
