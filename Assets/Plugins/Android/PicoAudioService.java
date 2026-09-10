package com.picobridge.audio;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/** Integrated form of pico_audio_bridge's PCM service; all audio I/O is off the Unity thread. */
public final class PicoAudioService extends Service {
    private static final String TAG = "PicoAudioBridge";
    private static final String CHANNEL = "pico_bridge_audio";
    private static final int RATE = 16000, SAMPLES = 160, BYTES = 320;
    private static final int TX_PORT = 50001, RX_PORT = 50002;
    private static volatile String status = "Audio off";
    private static volatile boolean muted;
    private static volatile Session current;
    // Serialize close/open across rapid stop/start Service instances as well as one instance.
    private static final ExecutorService lifecycle = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile boolean destroyed;
    private Session owned;

    public static void start(Activity activity, String host) {
        status = "Starting audio";
        Intent intent = new Intent(activity, PicoAudioService.class).putExtra("host", host);
        activity.startForegroundService(intent);
    }

    public static void stop(Activity activity) {
        activity.stopService(new Intent(activity, PicoAudioService.class));
        status = "Audio off";
    }

    public static void setMuted(boolean value) { muted = value; }

    public static String getStatus() {
        Session session = current;
        if (session == null || !session.running.get()) return status;
        return (muted ? "Mic muted" : "Audio on") + " | TX " + session.txCount.get()
                + " RX " + session.rxCount.get();
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public void onCreate() {
        super.onCreate();
        getSystemService(NotificationManager.class).createNotificationChannel(
                new NotificationChannel(CHANNEL, "Pico Bridge audio", NotificationManager.IMPORTANCE_LOW));
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        final String host = intent == null ? null : intent.getStringExtra("host");
        if (host == null || host.trim().isEmpty()) { stopSelf(); return START_NOT_STICKY; }
        Notification notification = new Notification.Builder(this, CHANNEL)
                .setContentTitle("Pico Bridge audio")
                .setContentText("Microphone and speaker connected to " + host)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now).setOngoing(true).build();
        try {
            if (Build.VERSION.SDK_INT >= 30)
                startForeground(101, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
                        | ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
            else startForeground(101, notification);
        } catch (RuntimeException error) {
            // Permission/foreground eligibility can change while the app is pausing.
            // Failure here must not crash the Unity tracking process.
            status = "Audio error: " + error.getMessage();
            Log.e(TAG, "Cannot start audio foreground service", error);
            stopSelf();
            return START_NOT_STICKY;
        }
        lifecycle.execute(() -> {
            closeCurrent();
            if (destroyed) return;
            Session session = new Session();
            owned = session;
            current = session;
            try {
                session.open(host);
                if (destroyed) { closeCurrent(); return; }
                session.startThreads();
                Log.i(TAG, "PCM started: " + host + ":" + TX_PORT + " / RX " + RX_PORT
                        + ", 16000 Hz mono, 320 bytes/10 ms");
            } catch (Exception error) { fail(session, error); }
        });
        // Never restart the microphone autonomously after the app has been killed.
        return START_NOT_STICKY;
    }

    private void fail(Session session, Exception error) {
        if (destroyed || current != session) return;
        Log.e(TAG, "Audio stopped", error);
        status = "Audio error: " + error.getMessage();
        main.post(() -> { if (!destroyed && current == session) stopSelf(); });
    }

    private void closeCurrent() {
        Session session = owned;
        if (session != null) session.close();
        if (current == session) current = null;
        owned = null;
    }

    @Override public void onDestroy() {
        destroyed = true;
        lifecycle.execute(this::closeCurrent);
        stopForeground(true);
        super.onDestroy();
    }

    private final class Session {
        final AtomicBoolean running = new AtomicBoolean(false);
        final AtomicLong txCount = new AtomicLong(), rxCount = new AtomicLong();
        final ArrayBlockingQueue<byte[]> queue = new ArrayBlockingQueue<>(50);
        InetAddress peer;
        DatagramSocket tx, rx;
        AudioRecord record;
        AudioTrack track;
        Thread captureThread, receiveThread, playbackThread;

        void open(String host) throws Exception {
            peer = InetAddress.getByName(host);
            // Bind before acquiring audio hardware: a second audio app gets an actionable error.
            rx = new DatagramSocket(RX_PORT);
            tx = new DatagramSocket();
            int inputBuffer = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            int outputBuffer = AudioTrack.getMinBufferSize(RATE, AudioFormat.CHANNEL_OUT_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (inputBuffer <= 0 || outputBuffer <= 0) throw new IllegalStateException("PCM format unsupported");
            record = new AudioRecord.Builder().setAudioSource(MediaRecorder.AudioSource.MIC)
                    .setAudioFormat(new AudioFormat.Builder().setSampleRate(RATE)
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setChannelMask(AudioFormat.CHANNEL_IN_MONO).build())
                    .setBufferSizeInBytes(Math.max(inputBuffer, BYTES * 8)).build();
            if (record.getState() != AudioRecord.STATE_INITIALIZED || record.getSampleRate() != RATE)
                throw new IllegalStateException("Microphone must support 16000 Hz mono");
            track = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(new AudioFormat.Builder().setSampleRate(RATE)
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setBufferSizeInBytes(Math.max(outputBuffer, BYTES * 8)).build();
            if (track.getState() != AudioTrack.STATE_INITIALIZED)
                throw new IllegalStateException("Speaker initialization failed");
            record.startRecording();
            track.play();
            running.set(true);
        }

        Thread launch(String name, CheckedTask action) {
            Thread thread = new Thread(() -> {
                try { action.run(); }
                catch (Exception error) { if (running.get()) fail(this, error); }
            }, name);
            thread.start();
            return thread;
        }

        void startThreads() {
            captureThread = launch("PicoMicUdpTx", () -> {
                short[] samples = new short[SAMPLES];
                byte[] bytes = new byte[BYTES];
                while (running.get()) {
                    int filled = 0;
                    while (filled < SAMPLES && running.get()) {
                        int n = record.read(samples, filled, SAMPLES-filled, AudioRecord.READ_BLOCKING);
                        if (n <= 0) throw new IllegalStateException("Microphone read failed: " + n);
                        filled += n;
                    }
                    if (!running.get()) break;
                    if (muted) Arrays.fill(bytes, (byte)0);
                    else for (int i=0; i<SAMPLES; i++) {
                        bytes[i*2] = (byte)samples[i];
                        bytes[i*2+1] = (byte)(samples[i] >>> 8);
                    }
                    tx.send(new DatagramPacket(bytes, bytes.length, peer, TX_PORT));
                    txCount.incrementAndGet();
                }
            });
            receiveThread = launch("PicoAudioUdpRx", () -> {
                byte[] buffer = new byte[2048];
                while (running.get()) {
                    DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                    rx.receive(packet);
                    if (!packet.getAddress().equals(peer) || packet.getLength() != BYTES) continue;
                    byte[] copy = Arrays.copyOfRange(packet.getData(), packet.getOffset(), packet.getOffset()+BYTES);
                    if (!queue.offer(copy)) { queue.poll(); queue.offer(copy); }
                    rxCount.incrementAndGet();
                }
            });
            playbackThread = launch("PicoSpeakerPlayback", () -> {
                while (running.get() && queue.size() < 5) Thread.sleep(5);
                while (running.get()) {
                    byte[] bytes = queue.take();
                    int offset = 0;
                    while (offset < bytes.length && running.get()) {
                        int n = track.write(bytes, offset, bytes.length-offset, AudioTrack.WRITE_BLOCKING);
                        if (n <= 0) throw new IllegalStateException("Speaker write failed: " + n);
                        offset += n;
                    }
                }
            });
        }

        void close() {
            running.set(false);
            if (rx != null) rx.close();
            if (tx != null) tx.close();
            try { if (record != null) record.stop(); } catch (Exception ignored) { }
            try { if (track != null) track.stop(); } catch (Exception ignored) { }
            for (Thread thread : new Thread[]{captureThread, receiveThread, playbackThread}) {
                if (thread == null) continue;
                thread.interrupt();
                try { thread.join(500); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            }
            if (record != null) record.release();
            if (track != null) track.release();
            queue.clear();
        }
    }

    private interface CheckedTask { void run() throws Exception; }
}
