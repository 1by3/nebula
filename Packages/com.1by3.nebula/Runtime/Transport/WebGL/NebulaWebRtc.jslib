// Browser side of Nebula's WebRTC transport (WebRtcClientTransport.cs). One RTCPeerConnection per link with two
// data channels agreed with the gateway in advance: id 0 reliable and ordered, id 1 unordered with no retransmissions.
// The offer is posted to the gateway's HTTP server, which answers as an ICE-lite endpoint listing its host
// candidates, so no STUN or TURN server is involved. Messages are queued here and pulled by C# every frame.
var NebulaWebRtc = {
  $NebulaRtc: {
    next: 1,
    links: {},
    connectTimeoutMs: 15000,
    reliable: 0,
    unreliable: 1,
    open: 1,
    closed: 2
  },

  NebulaRtc_Connect: function (hostPtr, port) {
    var host = UTF8ToString(hostPtr);
    var id = NebulaRtc.next++;
    var link = { state: 0, queue: [], rtt: -1, pc: null, channels: [], timers: [] };
    NebulaRtc.links[id] = link;
    var fail = function (why) {
      if (link.state !== NebulaRtc.closed) console.warn('[nebula] WebRTC link to ' + host + ':' + port + ' closed: ' + why);
      link.state = NebulaRtc.closed;
    };
    try {
      var pc = new RTCPeerConnection({ iceServers: [] });
      link.pc = pc;
      link.channels = [
        pc.createDataChannel('nebula-reliable', { negotiated: true, id: 0 }),
        pc.createDataChannel('nebula-unreliable', { negotiated: true, id: 1, ordered: false, maxRetransmits: 0 })
      ];
      var opened = 0;
      link.channels.forEach(function (channel, index) {
        channel.binaryType = 'arraybuffer';
        channel.onopen = function () {
          if (++opened === 2 && link.state === 0) link.state = NebulaRtc.open;
        };
        channel.onclose = function () { fail('data channel closed'); };
        channel.onmessage = function (e) {
          if (link.state !== NebulaRtc.closed && e.data instanceof ArrayBuffer) link.queue.push({ channel: index, data: new Uint8Array(e.data) });
        };
      });
      pc.onconnectionstatechange = function () {
        if (pc.connectionState === 'failed' || pc.connectionState === 'closed') fail('connection ' + pc.connectionState);
      };
      var scheme = window.location.protocol === 'https:' ? 'https:' : 'http:';
      var authority = host.indexOf(':') >= 0 ? '[' + host + ']' : host;
      var url = scheme + '//' + authority + ':' + port + '/nebula/rtc';
      pc.createOffer()
        .then(function (offer) { return pc.setLocalDescription(offer); })
        .then(function () {
          return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: pc.localDescription.sdp });
        })
        .then(function (response) {
          if (!response.ok) throw new Error(url + ' answered ' + response.status);
          return response.text();
        })
        .then(function (answer) { return pc.setRemoteDescription({ type: 'answer', sdp: answer }); })
        .catch(function (e) { fail(e && e.message ? e.message : String(e)); });
      link.timers.push(setTimeout(function () {
        if (link.state === 0) fail('timed out');
      }, NebulaRtc.connectTimeoutMs));
      // The selected candidate pair's round trip, from the ICE consent checks the browser keeps sending.
      link.timers.push(setInterval(function () {
        if (link.state !== NebulaRtc.open) return;
        pc.getStats().then(function (report) {
          report.forEach(function (s) {
            if (s.type === 'candidate-pair' && s.state === 'succeeded' && (s.nominated || s.selected) && typeof s.currentRoundTripTime === 'number')
              link.rtt = Math.round(s.currentRoundTripTime * 1000);
          });
        });
      }, 1000));
    } catch (e) {
      fail(e && e.message ? e.message : String(e));
    }
    return id;
  },

  NebulaRtc_State: function (id) {
    var link = NebulaRtc.links[id];
    return link ? link.state : NebulaRtc.closed;
  },

  NebulaRtc_NextSize: function (id) {
    var link = NebulaRtc.links[id];
    return link && link.queue.length > 0 ? link.queue[0].data.length : -1;
  },

  NebulaRtc_NextChannel: function (id) {
    var link = NebulaRtc.links[id];
    return link && link.queue.length > 0 ? link.queue[0].channel : -1;
  },

  NebulaRtc_Receive: function (id, bufferPtr, capacity) {
    var link = NebulaRtc.links[id];
    if (!link || link.queue.length === 0 || link.queue[0].data.length > capacity) return -1;
    var message = link.queue.shift().data;
    HEAPU8.set(message, bufferPtr);
    return message.length;
  },

  NebulaRtc_Send: function (id, channelIndex, dataPtr, offset, length) {
    var link = NebulaRtc.links[id];
    if (!link || link.state !== NebulaRtc.open) return 0;
    var channel = link.channels[channelIndex];
    if (!channel || channel.readyState !== 'open') return 0;
    // State is stale by the time a backlog clears: drop unreliable messages rather than queue them behind a slow link.
    if (channelIndex === NebulaRtc.unreliable && channel.bufferedAmount > 262144) return 0;
    try {
      channel.send(HEAPU8.slice(dataPtr + offset, dataPtr + offset + length));
      return 1;
    } catch (e) {
      return 0;
    }
  },

  NebulaRtc_RoundTripMs: function (id) {
    var link = NebulaRtc.links[id];
    return link ? link.rtt : -1;
  },

  NebulaRtc_Close: function (id) {
    var link = NebulaRtc.links[id];
    if (!link) return;
    delete NebulaRtc.links[id];
    link.state = NebulaRtc.closed;
    link.timers.forEach(function (t) { clearTimeout(t); clearInterval(t); });
    try {
      if (link.pc) link.pc.close();
    } catch (e) {}
  }
};

autoAddDeps(NebulaWebRtc, '$NebulaRtc');
mergeInto(LibraryManager.library, NebulaWebRtc);
