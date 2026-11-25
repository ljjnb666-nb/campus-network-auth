#include "cipher/CipherInterface.h"
#include "cipher/CipherUtils.h"

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include "aes_common.h"

typedef struct {
    uint8_t round_keys1[176];
    uint8_t round_keys2[176];
} aes_ecb_linux_ctx_t;

static char* aes_ecb_encrypt(cipher_interface_t* self, const char* text)
{
    if (!self || !text) return NULL;
    const aes_ecb_linux_ctx_t* ctx = self->private_data;
    const size_t len = s_strlen(text);
    const uint8_t* input = (const uint8_t*)text;
    size_t padded_len = 0;
    uint8_t* padded = pad_2_multiple(input, len, 16, &padded_len);
    if (!padded) return NULL;
    uint8_t* out = s_malloc(padded_len);
    for (size_t i = 0; i < padded_len; i += 16)
    {
        uint8_t tmp[16];
        aes128_encrypt_block(padded + i, tmp, ctx->round_keys2);
        aes128_encrypt_block(tmp, out + i, ctx->round_keys1);
        cipher_secure_zero(tmp, sizeof(tmp));
    }
    char* hex = bytes_2_hex(out, padded_len);
    s_secure_free(padded, padded_len);
    s_secure_free(out, padded_len);
    return hex;
}

static char* aes_ecb_decrypt(cipher_interface_t* self, const char* hex)
{
    if (!self || !hex) return NULL;
    const aes_ecb_linux_ctx_t* ctx = self->private_data;
    size_t in_len = 0;
    uint8_t* in = hex_2_bytes(hex, &in_len);
    if (!in || in_len % 16 != 0)
    {
        s_secure_free(in, in_len); return NULL;
    }
    uint8_t* out = s_malloc(in_len);
    for (size_t i = 0; i < in_len; i += 16)
    {
        uint8_t tmp[16];
        aes128_decrypt_block(in + i, tmp, ctx->round_keys1);
        aes128_decrypt_block(tmp, out + i, ctx->round_keys2);
        cipher_secure_zero(tmp, sizeof(tmp));
    }
    size_t plain_len = in_len;
    while (plain_len > 0 && out[plain_len - 1] == 0x00) plain_len--;
    char* text = s_malloc(plain_len + 1);
    memcpy(text, out, plain_len);
    text[plain_len] = '\0';
    s_secure_free(in, in_len);
    s_secure_free(out, in_len);
    return text;
}

static void aes_ecb_destroy(cipher_interface_t* self)
{
    if (!self) return;
    s_secure_free(self->private_data, sizeof(aes_ecb_linux_ctx_t));
    s_free(self);
}

cipher_interface_t* create_aes_ecb_cipher(const uint8_t* key1, const uint8_t* key2)
{
    if (!key1 || !key2) return NULL;
    cipher_interface_t* ci = s_calloc(1, sizeof(cipher_interface_t));
    aes_ecb_linux_ctx_t* ctx = s_calloc(1, sizeof(aes_ecb_linux_ctx_t));
    aes128_key_expansion(key1, ctx->round_keys1);
    aes128_key_expansion(key2, ctx->round_keys2);
    ci->encrypt = aes_ecb_encrypt;
    ci->decrypt = aes_ecb_decrypt;
    ci->destroy = aes_ecb_destroy;
    ci->private_data = ctx;
    return ci;
}