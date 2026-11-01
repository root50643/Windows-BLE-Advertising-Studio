# SPDX-License-Identifier: GPL-3.0-or-later
# Adapter derived from GoogleFindMyTools, Copyright (C) 2024 Leon Böttger.
# Changes: Windows identity input, rotating EIDs, bounded report retrieval, JSON IPC,
# isolated account cache, no firmware or hardware provisioning, no secret console output.
import base64
import contextlib
import hashlib
import json
import os
import secrets
import sys
import tempfile
import threading
import time
from datetime import datetime, timezone


def run(data, auth_file):
    sys.path.insert(0, os.environ['GOOGLE_TOOLS_ROOT'])
    import Auth.token_cache as cache
    cache._get_secrets_file = lambda: auth_file
    # Account authorization is interactive on the owner's own computer, never inside an API worker.
    import builtins
    def no_input(*_args, **_kwargs):
        raise RuntimeError('AuthorizationRequired')
    builtins.input = no_input
    from FMDNCrypto.eid_generator import generate_eid
    from FMDNCrypto.key_derivation import FMDNOwnerOperations
    from KeyBackup.cloud_key_decryptor import encrypt_aes_gcm, decrypt_aes_gcm
    from SpotApi.GetEidInfoForE2eeDevices.get_owner_key import get_owner_key
    from SpotApi.GetEidInfoForE2eeDevices.get_eid_info_request import get_eid_info
    from SpotApi.CreateBleDevice.config import mcu_fast_pair_model_id
    from SpotApi.CreateBleDevice.util import flip_bits
    from SpotApi.spot_request import spot_request
    from ProtoDecoders import DeviceUpdate_pb2 as pb, Common_pb2
    from ProtoDecoders.decoder import parse_device_list_protobuf, parse_device_update_protobuf
    from NovaApi.ListDevices.nbe_list_devices import request_device_list
    from NovaApi.ExecuteAction.LocateTracker.decrypt_locations import retrieve_identity_key

    identity, eik = data['identity'], base64.b64decode(data['secret'])
    if len(eik) != 32:
        raise ValueError('Invalid EIK')
    pair_date = int(datetime.fromisoformat(identity['pairDate'].replace('Z', '+00:00')).timestamp())

    def public_ids():
        # Same beacon-time origin as Windows. Do not repeat the static EID used by upstream firmware examples.
        start = max(0, int(time.time()) - pair_date - 3 * 3600) // 1024 * 1024
        result = []
        for offset in range(start, start + 4 * 86400, 1024):
            item = pb.PublicKeyIdList.PublicKeyIdInfo()
            item.timestamp.seconds = pair_date + offset
            item.publicKeyId.truncatedEid = generate_eid(eik, offset)[:10]
            result.append(item)
        return result

    def find_device():
        devices = parse_device_list_protobuf(request_device_list())
        for device in devices.deviceMetadata:
            registration = device.information.deviceRegistration
            if registration.fastPairModelId != mcu_fast_pair_model_id:
                continue
            ids = device.identifierInformation.canonicIds.canonicId
            if not ids or identity.get('canonicId') and ids[0].id != identity['canonicId']:
                continue
            if retrieve_identity_key(registration) == eik:
                return device
        return None

    if data['operation'] == 'selftest':
        announcements = public_ids()
        request = pb.UploadPrecomputedPublicKeyIdsRequest()
        request.deviceEids.add().clientList.publicKeyIdInfo.extend(announcements)
        assert request.SerializeToString() and len(announcements) > 300
        assert generate_eid(bytes(range(32)), 1024).hex() == '3a19ac7db9a3a9140c0faceae210ec57a127fb31'
        return {'status': 'Pinned upstream imports, protobuf and rotating EID self-test passed; no account/network verification', 'announcements': len(announcements)}
    device = find_device()
    if device is None and data['operation'] == 'register':
        owner_key = get_owner_key()
        request = pb.RegisterBleDeviceRequest()
        # Upstream's development model ID is retained for its custom-key format; this is not Fast Pair certification.
        request.fastPairModelId = mcu_fast_pair_model_id
        request.description.userDefinedName = identity['name']
        request.description.deviceType = pb.SpotDeviceType.DEVICE_TYPE_BEACON
        request.capabilities.isAdvertising = True
        request.capabilities.trackableComponents = request.capabilities.capableComponents = 1
        registration = request.e2eePublicKeyRegistration
        registration.rotationExponent = 10
        registration.pairingDate = pair_date
        user = registration.encryptedUserSecrets
        user.encryptedIdentityKey = flip_bits(encrypt_aes_gcm(owner_key, eik), True)
        user.encryptedAccountKey = secrets.token_bytes(44)
        user.encryptedSha256AccountKeyPublicAddress = secrets.token_bytes(60)
        user.ownerKeyVersion = get_eid_info().encryptedOwnerKeyAndMetadata.ownerKeyVersion
        user.creationDate.seconds = pair_date
        registration.publicKeyIdList.publicKeyIdInfo.extend(public_ids())
        request.manufacturerName, request.modelName = 'Windows BLE Advertising Studio', 'Windows native Bluetooth PC'
        keys = FMDNOwnerOperations(); keys.generate_keys(identity_key=eik)
        request.ringKey, request.recoveryKey, request.unwantedTrackingKey = keys.ringing_key, keys.recovery_key, keys.tracking_key
        spot_request('CreateBleDevice', request.SerializeToString())
        for _ in range(6):
            time.sleep(2)
            device = find_device()
            if device is not None:
                break
    if device is None:
        return {'code': 'GoogleOwnedIdentityNotFound'}
    canonic = device.identifierInformation.canonicIds.canonicId[0].id
    upload = pb.UploadPrecomputedPublicKeyIdsRequest()
    item = upload.deviceEids.add()
    item.canonicId.id, item.pairDate = canonic, pair_date
    item.clientList.publicKeyIdInfo.extend(public_ids())
    spot_request('UploadPrecomputedPublicKeyIds', upload.SerializeToString())
    if data['operation'] == 'register':
        return {'canonicId': canonic}

    from Auth.fcm_receiver import FcmReceiver
    from NovaApi.ExecuteAction.LocateTracker.location_request import create_location_request
    from NovaApi.nova_request import nova_request
    from NovaApi.scopes import NOVA_ACTION_API_SCOPE
    from NovaApi.util import generate_random_uuid
    from FMDNCrypto.foreign_tracker_cryptor import decrypt
    request_id, completed, responses = generate_random_uuid(), threading.Event(), []
    def callback(response):
        update = parse_device_update_protobuf(response)
        if update.fcmMetadata.requestUuid == request_id:
            responses.append(update); completed.set()
    fcm_token = FcmReceiver().register_for_location_updates(callback)
    nova_request(NOVA_ACTION_API_SCOPE, create_location_request(canonic, fcm_token, request_id))
    if not completed.wait(45):
        return {'code': 'GoogleLocationResponseTimeout'}
    update = responses[0]
    # Reject a reply for any other registered key, even if an upstream response was misrouted.
    if retrieve_identity_key(update.deviceMetadata.information.deviceRegistration) != eik:
        return {'code': 'GoogleResponseIdentityMismatch'}
    locations = update.deviceMetadata.information.locationInformation.reports.recentLocationAndNetworkLocations
    pairs = list(zip(locations.networkLocations, locations.networkLocationTimestamps))
    if locations.HasField('recentLocation'):
        pairs.append((locations.recentLocation, locations.recentLocationTimestamp))
    reports = []
    for location, timestamp in pairs:
        if location.status == Common_pb2.Status.SEMANTIC:
            continue  # A place name is not a coordinate fix.
        geo, encrypted = location.geoLocation, location.geoLocation.encryptedReport
        try:
            clear = decrypt_aes_gcm(hashlib.sha256(eik).digest(), encrypted.encryptedLocation) if not encrypted.publicKeyRandom else decrypt(eik, encrypted.encryptedLocation, encrypted.publicKeyRandom, geo.deviceTimeOffset)
            point = pb.Location(); point.ParseFromString(clear)
            reports.append(dict(trackerId=identity['id'], observedAt=datetime.fromtimestamp(timestamp.seconds, timezone.utc).isoformat(),
                latitude=point.latitude / 1e7, longitude=point.longitude / 1e7, accuracyMeters=geo.accuracy,
                source='Google Find Hub', cryptographicallyVerified=True,
                advertisedIdentityHex=generate_eid(eik, geo.deviceTimeOffset).hex() if encrypted.publicKeyRandom else None,
                reportId=hashlib.sha256(canonic.encode() + encrypted.encryptedLocation + str(timestamp.seconds).encode()).hexdigest()))
        except Exception:
            continue
    return {'canonicId': canonic, 'reports': reports}


if __name__ == '__main__':
    os.umask(0o077)
    request = {'operation': 'selftest', 'identity': {'pairDate': datetime.now(timezone.utc).isoformat()}, 'secret': base64.b64encode(bytes(range(32))).decode(), 'auth': {}} if '--self-test' in sys.argv else json.loads(sys.stdin.read())
    with tempfile.TemporaryDirectory(prefix='ble-account-') as directory:
        account = os.path.join(directory, 'secrets.json')
        with open(account, 'w') as f:
            json.dump(request['auth'], f)
        with open(os.devnull, 'w') as silent, contextlib.redirect_stdout(silent), contextlib.redirect_stderr(silent):
            try:
                result = run(request, account)
            except BaseException:
                result = {'code': 'GoogleAuthorizationOrProtocolFailure'}
        try:
            with open(account) as f:
                result['auth'] = json.load(f)
        except (OSError, ValueError):
            pass
    sys.stdout.write(json.dumps(result)); sys.stdout.flush()
    os._exit(0)  # Upstream FCM helper threads must not keep this bounded worker alive.
