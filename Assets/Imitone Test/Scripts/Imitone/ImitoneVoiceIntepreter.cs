using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using B83.MathHelpers;
//using System.Text.Json;        
//using System.Text.Json.Nodes;
using Defective.JSON;

using imitone;
//use this to translate the voice intepreter stuff into imitone
//copy functions from voiceinterpreter to here.

public class ImitoneVoiceIntepreter: MonoBehaviour
{
    //base variables pitch and midiNote
    public float pitch_hz = 0f;
    public float note_st = 0f;

    //coped variables from old Voice Intepreter
    public Action<float> OnNewTone;
    public Action ChantEvent;
    public Action BreathEvent;
    
    [Tooltip("Active when toning.")]
    public bool Active { get; private set; }
    

    [Tooltip("Toning With False Positive Logic")]
    public bool toneActive { get; private set; }
    
    [Tooltip("Confident Toning")]
    public bool toneActiveConfident { get; private set; }

    [SerializeField] private float positiveActiveThreshold1 = 0.05f;
    [SerializeField] private float positiveActiveThreshold2 = 0.45f;
    [SerializeField] private float negativeActiveThreshold = 0.1f;  // Added missing semicolon
    private float activeTimer = 0f;
    private float inactiveTimer = 0f;
    private float confidentActiveTimer = 0.0f;
    private float confidentInactiveTimer = 0.0f;

    //TODO: using these vars
    public float ssVolume { get; private set; }
    public float cChantCharge => _cChantCharge;
    
    //public float Cadence => _lengthOfLastBreath == 0 ? 0 : (_lengthOfTonesSinceBreath / _lengthOfLastBreath);
    [SerializeField] private float _cadence;
    private float _lengthOfTones;
    private float _lengthOfBreath;
    private float _tThisTone;
    private float _tThisRest;
    private float _durLastTone;    


    [SerializeField] private float _breathHoldTimeBeforeInhale;
    public float _inhaleDuration;
    //Both Above is the duration of breath 
    private float _breathVolume;
    private bool isResettingTone = false;
    public float _breathVolumeTotal = 0f;
    public int breathStage = 0;
    

    public int MostRecentSemitone => _semitone;
    public string MostRecentSemitoneNote => _semitoneNote;
    private int _semitone;
    private string _semitoneNote;
    private int[] _mostRecentSemitone = new []{-1,-1};
    private int[] _previousSemitone = new []{-1,-1};
    

    private float maximumDBfloat = 0.0f;
    private float minimumDBfloat = -68.0f;
    private float UpperVoiceThresholdDB = -35.0f;
    // we only assume a voice if it rises above upperthreshold
    private float lowerVoiceThresholdDB = -45.0f;

    //if input is is 12db higher then UpperVoiceThresholdDB then UpperVoiceThresholdDB is going to 
    //move upwards at linear rate. If input is 10db lower than the uppervoicethresholdDB, will go down
    // at linear and damp rate.

    //


    [Header("Noise")]
    [SerializeField] private float _thresholdLerpValue;
    
    [Header("DampingValues")]
    [SerializeField]float velocity = 0.0f;
    [SerializeField]float damp = 0.1f;

    private float _harmonicity = 0.0f;
    private float _elapsedTimeWithoutTone = 0.0f;
    private bool _chanting;
    private float _cChantCharge;
    private float _cChantLerpFast;
    private float _cChartLerpSlow;
    private float _rmsValue;
    public float _dbValue = -80.0f;
    public float _level; 
    public float _dbThreshold = -25.0f;
    private const int SAMPLE_SIZE = 1024;
    private AudioSource _audioSource;
    private string _selectedDevice; 
    private int _sampleRate;
    private readonly float _referenceAmplitude = 20.0f * Mathf.Pow(10.0f, -6.0f);
    [SerializeField] private AudioClip _audioClip;
    [SerializeField] private float _pitchDifference = 3;
    
    private Dictionary<int, float> _breathVolumeContributions = new Dictionary<int, float>();
    private int _coroutineCounter = 0; // To generate unique keys

    [TextAreaAttribute(8,8)] public string imitoneState;

    [Header("dbController")]
    private bool expectNoiseFloor = false;
    private bool manualMode = false;

    


    int sampleRate;
    ImitoneVoice imitone;

    string             microphoneName;
    AudioClip          inputBuffer;
    int                micPosRead = 0;
    float[]            capturedInput;

    // Start is called before the first frame update
    void Start()
    {
        foreach (var device in Microphone.devices)
            {microphoneName = device; break;}
        if (microphoneName.Length == 0)
        {
            Debug.Log("No microphone was available for pitch tracking.");
            return;
        }
        Debug.Log("Chose microphone: " + microphoneName);
        Debug.Log(Microphone.devices);
        // NOTE: Unity doesn't give us a way to query native samplerate.
        //  Converting to 48khz may degrade audio quality slightly.
        sampleRate = 48000;

        // NOTE: this requires permission on mobile.
        
        inputBuffer = Microphone.Start(
                deviceName: microphoneName,
                loop:       true,
                lengthSec:  1,
                frequency:  sampleRate
                );

        if (inputBuffer == null)
        {
            Debug.Log("PitchTracker failed to Start recording from Microphone!");
            return;
        }
        
        try
        {
            ImitoneVoice.ActivateLicense("imitone technology used under license to New Entheogen Ltd, March 2023.");

            // Create an imitone voice whose notes are in "exact pitch" mode.
            // Also specify 'range' large enough to permit whistling.
            imitone = new ImitoneVoice(sampleRate, "{\"guide\":\"off\",\"slide\":\"bend\",\"range\":{\"min\":34.0,\"max\":101.0}}");
        }
        catch (System.Exception e)
        {
            Debug.Log(e);
            throw;
        }

        if (imitone == null)
        {
            Debug.Log("imitone was null after creation.");
        }
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        //_cadence = _lengthOfLastBreath == 0 ? 0 : (_lengthOfTonesSinceBreath / _lengthOfLastBreath);
        CheckToning();
        if (!inputBuffer) return;

        // The microphone's write position in the clip can wrap back around to the beginning.
        int micPosWrite = Microphone.GetPosition(microphoneName);
        Array.Resize(ref capturedInput, (inputBuffer.samples  +  micPosWrite - micPosRead) % inputBuffer.samples);
        if (capturedInput.Length > 0)
        {
            // Read the latest audio data, beginning from where we left off and wrapping around as needed.
            inputBuffer.GetData(capturedInput, micPosRead);
            micPosRead = (micPosRead + capturedInput.Length) % inputBuffer.samples;

            // Analyze the captured audio with imitone.
            if (imitone != null)
            {
                float peakAmplitude = 0f;
                foreach (float sample in capturedInput)
                    if (Math.Abs(sample) > peakAmplitude)
                        peakAmplitude = Math.Abs(sample);
                //Debug.Log(String.Format("Analyzing mic samples x {0}, peak amplitude {1}", capturedInput.Length, peakAmplitude));
                imitone.InputAudio(capturedInput);
                imitoneState = imitone.GetState();
                try
                {
                    var data = new JSONObject(imitoneState);
                    JSONObject tones = data["tones"];
                    JSONObject notes = data["notes"];
                    if (!tones || !tones.isArray) throw new ArgumentException("imitone output did not include tones array.");
                    if (!notes || !notes.isArray) throw new ArgumentException("imitone output did not include notes array.");
                    if (tones.list != null && tones.list.Count > 0)
                    {
                        var tone = tones[0];
                            if(tone.HasField("sound")){
                                var soundObject = tone.GetField("sound");
                                if(soundObject.HasField("power"))
                                {
                                    float power = soundObject.GetField("power").floatValue;
                                    _dbValue = (float)(10.0 * Math.Log10(power));
                                    _level = (float)Math.Pow(10,_dbValue) * 0.05f;
                                }
                            }
                            if(tone.HasField("sahir")){
                                var SahirObject = tone.GetField("sahir");
                                if(SahirObject.HasField("conv"))
                                {
                                    _harmonicity = SahirObject.GetField("conv").floatValue;
                                }
                            } 
                        if (!tone.isObject) throw new ArgumentException("imitone tone is not an object");
                        if (tone["frequency_hz"] == null) throw new ArgumentException("imitone tone does not have frequency_hz");
                        pitch_hz = tone["frequency_hz"].floatValue;
                    }
                    else
                    {
                        pitch_hz = 0f;
                    }
                    if (notes.list != null && notes.list.Count > 0)
                    {
                        var note = notes[0];
                        if (!note.isObject) throw new ArgumentException("imitone note is not an object");
                        if (note["pitch"] == null) throw new ArgumentException("imitone note does not have frequency_hz");
                        // Convert from imitone's wacky pitch value to MIDI frequency format
                        note_st = note["pitch"].floatValue / 100f - 36.3763165623f;
                    }
                    else
                    {
                        note_st = 0f;
                    }
                }
                catch (Exception e)
                {
                    Debug.Log(e);
                    pitch_hz = -1f;
                    note_st = -1f;
                }
                
            }
            else
            {
                //Debug.Log("No imitone voice to analyze audio.");
            }
        }
        if(toneActive)
        {
            _tThisTone += Time.deltaTime;
            _inhaleDuration = _tThisTone * 0.41f;
            ChantEvent?.Invoke();
            _cChantCharge += Time.deltaTime; 
        } 
        else
        {
            
            BreathEvent?.Invoke();
            _cChantCharge = CurveUtility.Damp(_cChantCharge, 0, ref velocity, damp);
        }
    }

     private void CheckToning(){
        if(_dbValue > UpperVoiceThresholdDB)
        {
            breathStage = 0;
            Active = true;
            if (!Active)
            {
                confidentInactiveTimer += Time.deltaTime;
                inactiveTimer += Time.deltaTime;
                activeTimer = 0f;
                confidentActiveTimer = 0.0f;
                if(inactiveTimer > negativeActiveThreshold)
                {
                    toneActive = false;
                    toneActiveConfident = false;
                }
            } 
            else
            {
                confidentActiveTimer += Time.deltaTime;
                activeTimer += Time.deltaTime;
                inactiveTimer = 0f;
                if (activeTimer >= positiveActiveThreshold1 && !toneActive)
                {
                    toneActive = true;
                    _inhaleDuration = 0.0f;
                    _tThisTone = 0.0f;
                }
                if(activeTimer >= positiveActiveThreshold2)
                {
                        toneActiveConfident = true;
                }
            }
            //TODO: determine when to start note
            _mostRecentSemitone = SemitoneUtility.GetSemitoneFromFrequency(pitch_hz);
            _semitone = SemitoneUtility.GetNoteFromSemitone(_mostRecentSemitone[0], _mostRecentSemitone[1]);
            _semitoneNote = SemitoneUtility.ToString(_mostRecentSemitone);
            if (!(_mostRecentSemitone[0] < 0) && (_previousSemitone[0] != _mostRecentSemitone[0] ||
                                                  _previousSemitone[1] != _mostRecentSemitone[1]))
            {
                _previousSemitone = _mostRecentSemitone;
                OnNewTone?.Invoke(_semitone);
            }
        }
        else if (_dbValue < -35.0f)
        {
            Active = false;
            handleBreathStage();
            if (!Active)
            {
                inactiveTimer += Time.deltaTime;
                confidentInactiveTimer += Time.deltaTime;
                activeTimer = 0f;
                confidentActiveTimer = 0f;
                if(inactiveTimer >= negativeActiveThreshold)
                {
                    toneActiveConfident = false;
                    toneActive = false;
                }
            }
        }
        if (!Active && !toneActive && !isResettingTone)
        {
            isResettingTone = true;
            StoppedToning();
            float currentInhaleDuration = _inhaleDuration;
            StartCoroutine(BreathVolumeCoroutine(currentInhaleDuration));
        }
        else if (Active || toneActive)
        {
            isResettingTone = false;
        }
    }

private void StoppedToning()
{
    if (_inhaleDuration < 1.76f)
    {
        _inhaleDuration = 1.76f;
    }
    else if (_inhaleDuration > 7.0f)
    {
        _inhaleDuration = 7.0f;
    }
}   

private void UpdateBreathVolumeTotal()
{
    _breathVolumeTotal = 0f;
    foreach (var contribution in _breathVolumeContributions.Values)
    {   
        _breathVolumeTotal += contribution;
    }
    _breathVolumeTotal = Mathf.Clamp(_breathVolumeTotal, 0.0f, 1.0f);
}

private IEnumerator BreathVolumeCoroutine(float inhaleDuration) {
    int coroutineID = _coroutineCounter++;
    _breathVolumeContributions[coroutineID] = 0f;

    float elapsedTime = 0f;

    while (elapsedTime < inhaleDuration) {
        float normalizedTime = elapsedTime / inhaleDuration;
        float currentBreathValue = (1 - Mathf.Cos(normalizedTime * 2 * Mathf.PI)) * 0.5f;

        _breathVolumeContributions[coroutineID] = currentBreathValue;
        UpdateBreathVolumeTotal();

        elapsedTime += Time.deltaTime;
        yield return null;
    }

    _breathVolumeContributions.Remove(coroutineID);
    UpdateBreathVolumeTotal();
}

private void handleBreathStage(){
    if(breathStage == 0){
        breathStage = 1;
    } else if(breathStage == 1){
        if(_breathVolumeTotal > 0 && 0.5f > _breathVolumeTotal){
            breathStage = 2;
        }
    } else if (breathStage == 2){
        if(_breathVolumeTotal > 0.5f){
            breathStage = 3;
        }
    } else if (breathStage == 3){
        if(_breathVolumeTotal < 0.5f && _breathVolumeTotal > 0){
            breathStage = 4;
        }
    } else if (breathStage == 4 || breathStage == 3){
        if(_breathVolumeTotal <= 0){
            breathStage = 5;
        }
    }
}
}
