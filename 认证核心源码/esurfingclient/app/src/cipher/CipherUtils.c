#include "cipher/CipherUtils.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

char* bytes_2_hex(const uint8_t* bytes, const size_t len)
{
    if (!bytes || len == 0 || len > (SIZE_MAX - 1) / 2) return NULL;
    char* hex = s_malloc(len * 2 + 1);
    if (!hex) return NULL;
    for (size_t i = 0; i < len; i++) sprintf(hex + i * 2, "%02X", bytes[i]);
    hex[len * 2] = '\0';
    return hex;
}

uint8_t* hex_2_bytes(const char* hex, size_t* out_len)
{
    if (!out_len) return NULL;
    *out_len = 0;
    if (!hex) return NULL;
    const size_t hex_len = strlen(hex);
    if (hex_len % 2 != 0) return NULL;
    const size_t byte_len = hex_len / 2;
    uint8_t* bytes = s_malloc(byte_len);
    if (!bytes) return NULL;
    for (size_t i = 0; i < byte_len; i++)
    {
        const unsigned char hi = (unsigned char)hex[i * 2];
        const unsigned char lo = (unsigned char)hex[i * 2 + 1];
        if (!isxdigit(hi) || !isxdigit(lo))
        {
            s_secure_free(bytes, byte_len);
            return NULL;
        }
        char byte_str[3] = {(char)hi, (char)lo, '\0'};
        bytes[i] = (uint8_t)strtol(byte_str, NULL, 16);
        cipher_secure_zero(byte_str, sizeof(byte_str));
    }
    *out_len = byte_len;
    return bytes;
}

void* s_malloc(const size_t size)
{
    if (size == 0) return NULL;
    void* ptr = malloc(size);
    if (!ptr)
    {
        fprintf(stderr, "内存分配失败: %zu bytes\n", size);
        exit(EXIT_FAILURE);
    }
    return ptr;
}

void* s_calloc(const size_t count, const size_t size)
{
    if (count == 0 || size == 0) return NULL;
    void* ptr = calloc(count, size);
    if (!ptr)
    {
        fprintf(stderr, "内存分配失败: %zu * %zu bytes\n", count, size);
        exit(EXIT_FAILURE);
    }
    return ptr;
}

void s_free(void* ptr)
{
    if (ptr) free(ptr);
}

void s_secure_free(void* ptr, size_t len)
{
    if (!ptr) return;
    cipher_secure_zero(ptr, len);
    free(ptr);
}

size_t s_strlen(const char* str)
{
    return str ? strlen(str) : 0;
}

uint8_t* pkcs7_padding(const uint8_t* data, const size_t data_len, const size_t block_size, size_t* out_len)
{
    if (!data || !out_len || block_size == 0 || block_size > UINT8_MAX) return NULL;
    size_t padding = block_size - data_len % block_size;
    if (padding == 0) padding = block_size;
    if (padding > SIZE_MAX - data_len) return NULL;
    const size_t padded_len = data_len + padding;
    uint8_t* padded = s_malloc(padded_len);
    memcpy(padded, data, data_len);
    memset(padded + data_len, (uint8_t)padding, padding);
    *out_len = padded_len;
    return padded;
}

uint8_t* rm_pkcs7_padding(const uint8_t* data, const size_t data_len, size_t* out_len)
{
    if (!out_len) return NULL;
    *out_len = 0;
    if (!data || data_len == 0) return NULL;
    const uint8_t padding = data[data_len - 1];
    if (padding == 0 || padding > data_len) return NULL;
    for (size_t i = data_len - padding; i < data_len; i++) if (data[i] != padding) return NULL;
    const size_t unpadded_len = data_len - padding;
    /* An all-padding block represents an empty but valid plaintext. */
    uint8_t* unpadded = s_malloc(unpadded_len ? unpadded_len : 1);
    if (unpadded_len == 0 && unpadded) unpadded[0] = 0;
    if (!unpadded) return NULL;
    memcpy(unpadded, data, unpadded_len);
    *out_len = unpadded_len;
    return unpadded;
}

uint8_t* pad_2_multiple(const uint8_t* data, const size_t data_len, const size_t multiple, size_t* out_len)
{
    if (!data || !out_len || multiple == 0) return NULL;
    const size_t padding = (multiple - data_len % multiple) % multiple;
    if (padding > SIZE_MAX - data_len) return NULL;
    const size_t padded_len = data_len + padding;
    uint8_t* padded = s_calloc(padded_len, 1);
    if (!padded) return NULL;
    memcpy(padded, data, data_len);
    *out_len = padded_len;
    return padded;
}

uint32_t uint32_bswap(const uint32_t value)
{
    return (value & 0x000000FFu) << 24 |
           (value & 0x0000FF00u) << 8 |
           (value & 0x00FF0000u) >> 8 |
           (value & 0xFF000000u) >> 24;
}

uint32_t bytes_2_uint32_be(const uint8_t* bytes)
{
    if (!bytes) return 0;
    return (uint32_t)bytes[0] << 24 |
           (uint32_t)bytes[1] << 16 |
           (uint32_t)bytes[2] << 8 |
           (uint32_t)bytes[3];
}

uint32_t bytes_2_uint32_le(const uint8_t* bytes)
{
    if (!bytes) return 0;
    return (uint32_t)bytes[3] << 24 |
           (uint32_t)bytes[2] << 16 |
           (uint32_t)bytes[1] << 8 |
           (uint32_t)bytes[0];
}

void uint32_2_bytes_be(const uint32_t value, uint8_t* bytes)
{
    if (!bytes) return;
    bytes[0] = (uint8_t)(value >> 24);
    bytes[1] = (uint8_t)(value >> 16);
    bytes[2] = (uint8_t)(value >> 8);
    bytes[3] = (uint8_t)value;
}

void uint32_2_bytes_le(const uint32_t value, uint8_t* bytes)
{
    if (!bytes) return;
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
    bytes[2] = (uint8_t)(value >> 16);
    bytes[3] = (uint8_t)(value >> 24);
}

void xor_bytes(const uint8_t* a, const uint8_t* b, uint8_t* result, const size_t len)
{
    if (!a || !b || !result) return;
    for (size_t i = 0; i < len; i++) result[i] = a[i] ^ b[i];
}

/* Exercise public cipher ownership with synthetic data only. No network. */
bool des_buffer_lifecycle_self_test(void)
{
    uint8_t key[48], iv[8];
    for (size_t i = 0; i < sizeof(key); ++i) key[i] = (uint8_t)(i + 1);
    memset(iv, 0x5a, sizeof(iv));
    const char* messages[] = {"a", "1234567", "12345678", "123456789",
        "1234567890123456", "1234567890123456789012345678901"};
    bool ok = true;
    for (unsigned mode = 0; mode < 5; ++mode)
    {
        cipher_interface_t* ci = NULL;
        switch (mode)
        {
        case 0: ci = create_desede_ecb_cipher(key, key + 24); break;
        case 1: ci = create_desede_cbc_cipher(key, key + 24, iv, iv); break;
        case 2: ci = create_desede_double_cbc_cipher(key, iv); break;
        case 3: ci = create_des_ecb_six_cipher(key); break;
        case 4: ci = create_des_ecb_six_keys_cipher(key, key + 8, key + 16,
                    key + 24, key + 32, key + 40); break;
        }
        if (!ci) { ok = false; break; }
        for (size_t i = 0; i < sizeof(messages) / sizeof(messages[0]); ++i)
        {
            char* encoded = ci->encrypt(ci, messages[i]);
            char* decoded = encoded ? ci->decrypt(ci, encoded) : NULL;
            const size_t expected = ((strlen(messages[i]) + 7) / 8) * 16;
            if (!encoded || strlen(encoded) != expected ||
                !decoded || strcmp(decoded, messages[i]) != 0) ok = false;
            if (encoded) s_secure_free(encoded, strlen(encoded) + 1);
            if (decoded) s_secure_free(decoded, strlen(decoded) + 1);
            if (!ok) break;
        }
        char* invalid = ci->decrypt(ci, "00");
        if (invalid) { s_secure_free(invalid, strlen(invalid) + 1); ok = false; }
        ci->destroy(ci);
        if (!ok) break;
    }
    cipher_secure_zero(key, sizeof(key));
    cipher_secure_zero(iv, sizeof(iv));
    return ok;
}


/* Remaining algorithm families: exercise only public synthetic keys and text. */
bool remaining_cipher_lifecycle_self_test(void)
{
    uint8_t key[48], iv[16];
    uint32_t words[12], iv_words[2] = {UINT32_C(0x12345678), UINT32_C(0x89abcdef)};
    for (size_t i = 0; i < sizeof(key); ++i) key[i] = (uint8_t)(i + 1);
    for (size_t i = 0; i < 12; ++i) words[i] = (uint32_t)(i + 1);
    memset(iv, 0x5a, sizeof(iv));
    const char* messages[] = {"a","1234567","12345678","123456789","123456789012345","1234567890123456","12345678901234567","1234567890123456789012345678901"};
    bool ok = true;
    for (unsigned mode = 0; mode < 16 && ok; ++mode)
    {
        cipher_interface_t* ci = NULL;
        switch (mode)
        {
        case 0: ci = create_aes_ecb_cipher(key, key + 16); break;
        case 1: ci = create_aes_cbc_cipher(key, key + 16, iv); break;
        case 2: ci = create_aes_double_ecb_cipher(key); break;
        case 3: ci = create_aes_double_cbc_cipher(key, iv); break;
        case 4: ci = create_sm4_ecb_cipher(key); break;
        case 5: ci = create_sm4_cbc_cipher(key, iv); break;
        case 6: ci = create_sm4_variant_ecb_cipher(key); break;
        case 7: ci = create_sm4_variant_cbc_cipher(key, iv); break;
        case 8: ci = create_mod_xtea_cipher(words, words + 4, words + 8, false); break;
        case 9: ci = create_mod_xtea_cipher(words, words + 4, words + 8, true); break;
        case 10: ci = create_mod_xtea_iv_cipher(words, words + 4, words + 8, iv_words); break;
        case 11: ci = create_mod_xtea_cbc_triple_cipher(words, words + 4, words + 8, iv_words); break;
        case 12: ci = create_tea_triple_ecb_cipher(key); break;
        case 13: ci = create_tea_triple_cbc_cipher(key, iv); break;
        case 14: ci = create_zuc_cipher(key, iv); break;
        case 15: ci = create_snow3g_variant_cipher(key, iv); break;
        }
        if (!ci) { ok = false; break; }
        for (size_t i = 0; i < sizeof(messages) / sizeof(messages[0]); ++i)
        {
            char* enc = ci->encrypt(ci, messages[i]);
            char* dec = enc ? ci->decrypt(ci, enc) : NULL;
            if (!enc || !dec || strcmp(dec, messages[i]) != 0) ok = false;
            s_secure_free_string(enc);
            s_secure_free_string(dec);
            if (!ok) break;
        }
        char* invalid = ci->decrypt(ci, "0G");
        if (invalid) { s_secure_free_string(invalid); ok = false; }
        ci->destroy(ci);
    }
    cipher_secure_zero(key, sizeof(key));
    cipher_secure_zero(iv, sizeof(iv));
    cipher_secure_zero(words, sizeof(words));
    cipher_secure_zero(iv_words, sizeof(iv_words));
    return ok;
}
